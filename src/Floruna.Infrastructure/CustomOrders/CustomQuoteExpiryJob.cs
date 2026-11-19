using Floruna.Application.Abstractions;
using Floruna.Application.CustomOrders;
using Floruna.Application.Messaging;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Floruna.Infrastructure.CustomOrders;

/// <summary>
/// Vence os orçamentos publicados cujo prazo passou (o preço de flor não vale para sempre).
/// A resposta do cliente já checa o prazo; o job só deixa o estado (e o painel) corretos.
/// </summary>
internal sealed partial class CustomQuoteExpiryJob(IServiceScopeFactory scopes, TimeProvider clock, Microsoft.Extensions.Options.IOptions<BackgroundJobsOptions> options, ILogger<CustomQuoteExpiryJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
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
        IReadOnlyList<(Guid TenantId, Guid QuoteId)> expired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            expired = await scope.ServiceProvider.GetRequiredService<ICustomOrderQueries>().DueQuotesAsync(clock.GetUtcNow(), BatchSize, cancellationToken);
        }

        var processed = 0;
        foreach (var (tenantId, quoteId) in expired)
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
            var result = await provider.GetRequiredService<ISender>().Send(new ExpireQuoteCommand(quoteId), cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} orçamento(s) vencido(s)")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no ciclo de expiração de orçamentos")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
