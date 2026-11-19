using Floruna.Application.Abstractions;
using Floruna.Application.Loyalty;
using Floruna.Application.Messaging;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Floruna.Infrastructure.Loyalty;

/// <summary>
/// Vence os lotes de cashback cujo prazo passou, registrando a expiração no razão.
/// Mesmo desenho do job de reservas: orientado a trabalho (todas as lojas numa consulta),
/// um escopo por conta e trava da conta: réplicas concorrentes não expiram em dobro.
/// O resgate já ignora lote vencido; o job só deixa o saldo e o passivo exatos.
/// </summary>
internal sealed partial class LoyaltyExpiryJob(IServiceScopeFactory scopes, TimeProvider clock, Microsoft.Extensions.Options.IOptions<BackgroundJobsOptions> options, ILogger<LoyaltyExpiryJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
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
        IReadOnlyList<(Guid TenantId, Guid AccountId)> expired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            expired = await scope.ServiceProvider.GetRequiredService<ILoyaltyQueries>().AccountsWithExpiredCreditsAsync(clock.GetUtcNow(), BatchSize, cancellationToken);
        }

        var processed = 0;
        foreach (var (tenantId, accountId) in expired)
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
            var result = await provider.GetRequiredService<ISender>().Send(new ExpireLoyaltyCreditsCommand(accountId), cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Cashback vencido em {Count} conta(s)")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no ciclo de expiração de cashback")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
