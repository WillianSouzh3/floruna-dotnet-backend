using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Application.Outbox;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Persistence;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Floruna.Infrastructure.Outbox;

/// <summary>
/// Entrega os eventos da outbox aos handlers (e-mail, e futuramente fidelidade, financeiro...).
/// </summary>
/// <remarks>
/// <para><b>Concorrência entre réplicas (Competing Consumers):</b> cada ciclo REIVINDICA um lote
/// com <c>UPDATE ... WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED) RETURNING</c>: duas réplicas
/// nunca pegam a mesma mensagem, e nenhuma espera a outra. A reivindicação é um LEASE de 5 min:
/// se a réplica morrer no meio, a mensagem volta a ficar disponível sozinha.</para>
/// <para><b>Falha:</b> backoff exponencial (30 s · 2ⁿ⁻¹, teto 1 h). Depois de
/// <see cref="MaxAttempts"/> tentativas, a mensagem vai para <c>Dead</c> (fila de mensagens mortas) e
/// gera log de ERRO para alerta. No Nest, um evento "envenenado" tentava para sempre sem ninguém
/// saber (dívida A2).</para>
/// <para><b>Desacoplamento:</b> o processador não conhece os consumidores. Cada módulo declara seus
/// handlers (<c>INotificationHandler&lt;DomainEventNotification&lt;T&gt;&gt;</c>); adicionar um efeito
/// novo não muda este arquivo (Open/Closed; no Nest o worker injetava 13 dependências, dívida M3).</para>
/// </remarks>
internal sealed partial class OutboxProcessor(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    Microsoft.Extensions.Options.IOptions<BackgroundJobsOptions> options,
    Application.Telemetry.FlorunaMetrics metrics,
    Application.Telemetry.OutboxHealth health,
    ILogger<OutboxProcessor> logger)
    : BackgroundService
{
    public const int MaxAttempts = 10;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private const int BatchSize = 20;

    private static readonly ConcurrentDictionary<string, Type?> EventTypes = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(PollInterval, clock);
        do
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
                await SampleHealthAsync(stoppingToken);
            }
#pragma warning disable CA1031 // um ciclo com erro não pode matar o processador
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogCycleFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Um ciclo: reivindica e processa um lote. Devolve quantas mensagens concluíram.</summary>
    public async Task<int> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(cancellationToken);
        var done = 0;
        foreach (var message in claimed)
        {
            if (await ProcessAsync(message, cancellationToken))
            {
                done++;
            }
        }

        return done;
    }

    /// <summary>
    /// Amostra a saúde da fila para os gauges (idade da mais antiga pendente, pendentes, mortas). Uma consulta
    /// por ciclo, no worker: o callback do gauge só lê o valor guardado, sem I/O.
    /// </summary>
    public async Task SampleHealthAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var now = clock.GetUtcNow();
        var oldest = await db.OutboxMessages.Where(m => m.State == OutboxState.Pending || m.State == OutboxState.Processing)
            .MinAsync(m => (DateTimeOffset?)m.OccurredAt, cancellationToken);
        health.OldestPendingAgeSeconds = oldest is { } o ? Math.Max(0, (now - o).TotalSeconds) : 0;
        health.Pending = await db.OutboxMessages.LongCountAsync(m => m.State == OutboxState.Pending || m.State == OutboxState.Processing, cancellationToken);
        health.DeadLetters = await db.OutboxMessages.LongCountAsync(m => m.State == OutboxState.Dead, cancellationToken);
    }

    private async Task<List<ClaimedMessage>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var now = clock.GetUtcNow();
        var leaseUntil = now + Lease;
        return await db.Database.SqlQuery<ClaimedMessage>($"""
            UPDATE outbox_messages SET state = 'Processing', locked_until = {leaseUntil}, attempts = attempts + 1
            WHERE id IN (
                SELECT id FROM outbox_messages
                WHERE (state = 'Pending' AND next_attempt_at <= {now})
                   OR (state = 'Processing' AND locked_until < {now})
                ORDER BY occurred_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING id, tenant_id, type, payload, attempts, trace_parent
            """).ToListAsync(cancellationToken);
    }

    private async Task<bool> ProcessAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        // Continua o trace de quem gerou o evento (o checkout, por exemplo): consumidor da "fila".
        System.Diagnostics.ActivityContext.TryParse(message.TraceParent, null, out var parent);
        using var activity = Application.Telemetry.FlorunaTelemetry.Source.StartActivity(
            $"outbox {message.Type[(message.Type.LastIndexOf('.') + 1)..]}", System.Diagnostics.ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "outbox");
        activity?.SetTag("messaging.message.id", message.Id.ToString());
        activity?.SetTag("floruna.outbox.attempt", message.Attempts);
        try
        {
            await DispatchAsync(message, cancellationToken);
            await UpdateAsync($"UPDATE outbox_messages SET state = 'Processed', processed_at = {clock.GetUtcNow()}, locked_until = NULL, last_error = NULL WHERE id = {message.Id}", cancellationToken);
            metrics.OutboxMessage("processed");
            return true;
        }
#pragma warning disable CA1031 // qualquer falha de handler vira retry/DLQ, nunca derruba o lote
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            var error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, error);
            if (message.Attempts >= MaxAttempts)
            {
                metrics.OutboxMessage("dead");
                LogDeadLettered(logger, ex, message.Id, message.Type, message.Attempts);
                await UpdateAsync($"UPDATE outbox_messages SET state = 'Dead', locked_until = NULL, last_error = {error} WHERE id = {message.Id}", cancellationToken);
            }
            else
            {
                metrics.OutboxMessage("retry");
                var delay = TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, message.Attempts - 1)));
                LogRetry(logger, message.Id, message.Type, message.Attempts, delay.TotalSeconds);
                var next = clock.GetUtcNow() + delay;
                await UpdateAsync($"UPDATE outbox_messages SET state = 'Pending', next_attempt_at = {next}, locked_until = NULL, last_error = {error} WHERE id = {message.Id}", cancellationToken);
            }

            return false;
        }
    }

    /// <summary>Entrega a cada handler separadamente, pulando os que já têm recibo.</summary>
    private async Task DispatchAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var eventType = EventTypes.GetOrAdd(message.Type, static name =>
            typeof(Domain.AssemblyReference).Assembly.GetType(name));
        if (eventType is null)
        {
            throw new InvalidOperationException($"Tipo de evento desconhecido: {message.Type}");
        }

        var domainEvent = (IDomainEvent)JsonSerializer.Deserialize(message.Payload, eventType, DomainEventsToOutboxInterceptor.Json)!;
        var notificationType = typeof(DomainEventNotification<>).MakeGenericType(eventType);
        var notification = Activator.CreateInstance(notificationType, domainEvent, message.Id)!;
        var handlerType = typeof(INotificationHandler<>).MakeGenericType(notificationType);
        var handle = handlerType.GetMethod(nameof(INotificationHandler<INotification>.Handle))!;

        // Descobre os handlers num escopo e executa CADA UM no seu próprio escopo (DbContext limpo).
        List<string> handlerNames;
        await using (var discovery = scopes.CreateAsyncScope())
        {
            handlerNames = [.. discovery.ServiceProvider.GetServices(handlerType).Select(h => h!.GetType().FullName!)];
        }

        foreach (var handlerName in handlerNames)
        {
            await using var scope = scopes.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var db = provider.GetRequiredService<FlorunaDbContext>();
            if (await db.OutboxReceipts.AnyAsync(r => r.MessageId == message.Id && r.Handler == handlerName, cancellationToken))
            {
                continue;
            }

            await EnterTenantAsync(provider, message.TenantId, cancellationToken);
            var handler = provider.GetServices(handlerType).Single(h => h!.GetType().FullName == handlerName)!;
            try
            {
                await (Task)handle.Invoke(handler, [notification, cancellationToken])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                // Relança a exceção REAL do handler preservando o stack trace original.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }

            db.OutboxReceipts.Add(new OutboxReceipt { MessageId = message.Id, Handler = handlerName, ProcessedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task EnterTenantAsync(IServiceProvider provider, Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            return;
        }

        var tenant = await provider.GetRequiredService<ITenantRepository>().GetByIdAsync(new TenantId(tenantId), cancellationToken)
            ?? throw new InvalidOperationException($"Loja {tenantId} não existe mais.");
        provider.GetRequiredService<TenantContext>().Set(new ResolvedTenant(
            tenant.Id, tenant.Slug.Value, tenant.Status, tenant.EffectivePlan(clock.GetUtcNow()), tenant.TimeZone));
    }

    private async Task UpdateAsync(FormattableString sql, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Database.ExecuteSqlAsync(sql, cancellationToken);
    }

    internal sealed record ClaimedMessage(Guid Id, Guid TenantId, string Type, string Payload, int Attempts, string? TraceParent);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox: mensagem {MessageId} ({Type}) foi para a fila de mortas após {Attempts} tentativas")]
    private static partial void LogDeadLettered(ILogger logger, Exception exception, Guid messageId, string type, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox: mensagem {MessageId} ({Type}) falhou na tentativa {Attempts}; nova tentativa em {DelaySeconds} s")]
    private static partial void LogRetry(ILogger logger, Guid messageId, string type, int attempts, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox: falha no ciclo de processamento")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
