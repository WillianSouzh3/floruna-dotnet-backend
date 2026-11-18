using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Application.Ordering;
using Floruna.Domain.Ordering;
using Floruna.Domain.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Floruna.Infrastructure.Ordering;

/// <summary>
/// Cancela pedidos pendentes que passaram do prazo de pagamento (RF-ORD-08), devolvendo
/// estoque, vaga de entrega e cupom. Usa o MESMO comando do painel, com ator <c>system</c>
/// e chave de idempotência fixa: rodar em duas réplicas ao mesmo tempo não cancela duas vezes.
/// </summary>
internal sealed partial class AbandonedOrdersJob(IServiceScopeFactory scopes, TimeProvider clock, Microsoft.Extensions.Options.IOptions<BackgroundJobsOptions> options, ILogger<AbandonedOrdersJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

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
#pragma warning disable CA1031
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(Floruna.SharedKernel.TenantId TenantId, OrderId OrderId)> abandoned;
        await using (var scope = scopes.CreateAsyncScope())
        {
            abandoned = await scope.ServiceProvider.GetRequiredService<IOrderRepository>().AbandonedAsync(clock.GetUtcNow(), 100, cancellationToken);
        }

        var cancelled = 0;
        foreach (var (tenantId, orderId) in abandoned)
        {
            await using var scope = scopes.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var tenant = await provider.GetRequiredService<ITenantRepository>().GetByIdAsync(tenantId, cancellationToken);
            if (tenant is null)
            {
                continue;
            }

            provider.GetRequiredService<TenantContext>().Set(new ResolvedTenant(
                tenant.Id, tenant.Slug.Value, tenant.Status, tenant.EffectivePlan(clock.GetUtcNow()), tenant.TimeZone));
            var result = await provider.GetRequiredService<ISender>().Send(new ChangeOrderStatusCommand(
                orderId.Value, "cancelado", "pendente", $"abandoned:{orderId}", "Pagamento não confirmado no prazo", null, "system"), cancellationToken);
            if (result.IsSuccess)
            {
                cancelled++;
            }
        }

        return cancelled;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no ciclo de pedidos abandonados")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
