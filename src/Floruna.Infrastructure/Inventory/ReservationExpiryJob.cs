using Floruna.Application.Abstractions;
using Floruna.Application.Inventory;
using Floruna.Application.Messaging;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Floruna.Infrastructure.Inventory;

/// <summary>
/// Devolve ao estoque as reservas cujo prazo venceu (pedido abandonado).
/// </summary>
/// <remarks>
/// <para>Orientado a TRABALHO, não a lojas: busca as reservas vencidas de todas as
/// lojas numa consulta só (índice parcial) e abre o contexto da loja de cada uma.
/// No Nest, os jobs varriam loja por loja a cada ciclo (custo O(lojas), dívida M4).</para>
/// <para>Várias réplicas podem rodar o job ao mesmo tempo (competing consumers):
/// a reserva é travada (<c>FOR UPDATE</c>) e a transição é idempotente, então a
/// segunda réplica simplesmente não encontra nada para fazer. Sem lock distribuído.</para>
/// </remarks>
internal sealed partial class ReservationExpiryJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReservationExpiryJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        IReadOnlyList<(Guid TenantId, Guid ReservationId)> expired;
        await using (var scope = scopes.CreateAsyncScope())
        {
            expired = await scope.ServiceProvider.GetRequiredService<IInventoryQueries>().ExpiredHeldReservationsAsync(BatchSize, cancellationToken);
        }

        var processed = 0;
        foreach (var (tenantId, reservationId) in expired)
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
            var result = await provider.GetRequiredService<ISender>().Send(new ExpireReservationCommand(reservationId), cancellationToken);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} reserva(s) vencida(s) devolvida(s) ao estoque")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no ciclo de expiração de reservas")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}
