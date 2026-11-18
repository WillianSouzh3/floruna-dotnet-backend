using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Domain.Catalog;
using Floruna.Domain.Inventory;
using Floruna.SharedKernel;

namespace Floruna.Application.Inventory;

/// <summary>
/// Reserva unidades para um dono (o checkout da Fase 4 usa isto). Idempotente pela
/// chave: repetir o mesmo pedido de reserva devolve a mesma reserva, sem segurar em dobro.
/// </summary>
public sealed record ReserveStockCommand(
    Guid ProductId, int Quantity, ReservationOwnerType OwnerType, string OwnerId, string IdempotencyKey, TimeSpan? HoldFor = null)
    : ICommand<StockReservationResponse>;

/// <summary>Liberação manual pelo painel (<c>POST /stock/reservations/:id/release</c>).</summary>
public sealed record ReleaseReservationCommand(Guid ReservationId, string By) : ICommand<ReservationEnvelope>;

/// <summary>Expira UMA reserva vencida (chamado pelo job, já no contexto da loja certa).</summary>
public sealed record ExpireReservationCommand(Guid ReservationId) : ICommand;

internal sealed class ReservationCommandHandlers(
    ITenantContext tenantContext,
    IInventoryRepository inventory,
    ICatalogCache cache,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<ReserveStockCommand, StockReservationResponse>,
      ICommandHandler<ReleaseReservationCommand, ReservationEnvelope>,
      ICommandHandler<ExpireReservationCommand>
{
    public async Task<Result<StockReservationResponse>> Handle(ReserveStockCommand request, CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var existing = await inventory.FindReservationByKeyAsync(request.IdempotencyKey, ct);
            if (existing is not null)
            {
                return Result.Success(existing);
            }

            var productId = new ProductId(request.ProductId);
            var item = await inventory.GetForUpdateAsync(productId, ct);
            var now = clock.GetUtcNow();
            if (item is null)
            {
                return Result.Failure<StockReservation>(InventoryErrors.InsufficientStock);
            }

            var held = item.Hold(request.Quantity, now);
            if (held.IsFailure)
            {
                return Result.Failure<StockReservation>(held.Error);
            }

            var reservation = StockReservation.Hold(tenantContext.RequiredTenantId, productId, request.Quantity, request.OwnerType,
                request.OwnerId, request.IdempotencyKey, now.Add(request.HoldFor ?? StockReservation.DefaultHold));
            inventory.Add(reservation);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(reservation);
        }, cancellationToken);

        if (result.IsFailure)
        {
            return result.Error;
        }

        await cache.InvalidateAsync(cancellationToken);
        return StockReservationResponse.From(result.Value);
    }

    public async Task<Result<ReservationEnvelope>> Handle(ReleaseReservationCommand request, CancellationToken cancellationToken)
    {
        var result = await SettleAsync(request.ReservationId, (r, now) => r.Release($"liberada por {request.By}", now), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error;
        }

        return result.Value is { } reservation
            ? new ReservationEnvelope(StockReservationResponse.From(reservation))
            : InventoryErrors.ReservationClosed;
    }

    public async Task<Result> Handle(ExpireReservationCommand request, CancellationToken cancellationToken)
    {
        var result = await SettleAsync(request.ReservationId, (r, now) => r.Expire(now), cancellationToken);
        return result.IsFailure ? result.Error : Result.Success();
    }

    /// <summary>
    /// Encerra a reserva e devolve as unidades ao estoque NA MESMA transação.
    /// Se ela já tinha sido encerrada (corrida com o checkout ou outro job),
    /// não devolve nada: a transição é idempotente.
    /// </summary>
    private async Task<Result<StockReservation?>> SettleAsync(Guid reservationId, Func<StockReservation, DateTimeOffset, bool> settle, CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var reservation = await inventory.GetReservationAsync(reservationId, ct);
            if (reservation is null)
            {
                return Result.Failure<StockReservation?>(InventoryErrors.ReservationNotFound);
            }

            var item = await inventory.GetForUpdateAsync(reservation.ProductId, ct);
            if (!settle(reservation, clock.GetUtcNow()))
            {
                return Result.Success<StockReservation?>(null);
            }

            item?.ReleaseHold(reservation.Quantity);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success<StockReservation?>(reservation);
        }, cancellationToken);

        if (result.IsSuccess && result.Value is not null)
        {
            await cache.InvalidateAsync(cancellationToken);
        }

        return result;
    }
}
