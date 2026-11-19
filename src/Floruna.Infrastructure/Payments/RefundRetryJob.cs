using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Application.Payments;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Floruna.Infrastructure.Payments;

/// <summary>
/// Retoma reembolsos sem resposta definitiva (timeout, 5xx) com a MESMA chave de idempotência:
/// se o gateway já tinha processado, devolve o mesmo reembolso; se não, processa agora.
/// Cada réplica reivindica a vez por lease (UPDATE condicional), então não há chamada dupla.
/// </summary>
internal sealed partial class RefundRetryJob(IServiceScopeFactory scopes, TimeProvider clock, Microsoft.Extensions.Options.IOptions<BackgroundJobsOptions> options, ILogger<RefundRetryJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
#pragma warning disable CA1031 // um ciclo com erro não pode matar o job
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogCycleFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Um ciclo. Público para os testes chamarem sem esperar o timer.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid TenantId, Guid RefundId)> expired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            expired = await scope.ServiceProvider.GetRequiredService<IRefundQueries>().DueAsync(clock.GetUtcNow(), BatchSize, cancellationToken);
        }

        var processed = 0;
        foreach (var (tenantId, refundId) in expired)
        {
            // Um escopo por item: falha num não contamina o DbContext do próximo.
            await using var scope = scopes.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var tenant = await provider.GetRequiredService<ITenantRepository>().GetByIdAsync(new TenantId(tenantId), cancellationToken);
            if (tenant is null)
            {
                continue;
            }

            provider.GetRequiredService<TenantContext>()
                .Set(new ResolvedTenant(tenant.Id, tenant.Slug.Value, tenant.Status, tenant.EffectivePlan(clock.GetUtcNow())));
            var result = await provider.GetRequiredService<ISender>().Send(new RetryRefundCommand(refundId), cancellationToken);
            if (result.IsSuccess)
            {
                processed++;
            }
        }

        if (processed > 0)
        {
            LogExpired(logger, processed);
        }

        return processed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} reembolso(s) retomado(s)")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no ciclo de retentativa de reembolsos")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
