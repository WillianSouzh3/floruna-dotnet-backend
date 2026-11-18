using Floruna.Application.Messaging;
using Floruna.SharedKernel;

namespace Floruna.Application.Inventory;

public sealed record GetMovementsQuery(Guid ProductId) : IQuery<MovementList>;

public sealed record GetLotsQuery(Guid ProductId) : IQuery<LotList>;

public sealed record GetExpiringLotsQuery(int Days) : IQuery<LotList>;

public sealed record GetLowStockQuery : IQuery<LowStockList>;

public sealed record GetLowStockCountQuery : IQuery<LowStockCount>;

public sealed record GetHeldReservationsQuery : IQuery<ReservationList>;

internal sealed class InventoryQueryHandlers(IInventoryQueries queries)
    : IQueryHandler<GetMovementsQuery, MovementList>,
      IQueryHandler<GetLotsQuery, LotList>,
      IQueryHandler<GetExpiringLotsQuery, LotList>,
      IQueryHandler<GetLowStockQuery, LowStockList>,
      IQueryHandler<GetLowStockCountQuery, LowStockCount>,
      IQueryHandler<GetHeldReservationsQuery, ReservationList>
{
    public async Task<Result<MovementList>> Handle(GetMovementsQuery request, CancellationToken cancellationToken) =>
        new MovementList(await queries.MovementsAsync(request.ProductId, cancellationToken));

    public async Task<Result<LotList>> Handle(GetLotsQuery request, CancellationToken cancellationToken) =>
        new LotList(await queries.ActiveLotsAsync(request.ProductId, cancellationToken));

    public async Task<Result<LotList>> Handle(GetExpiringLotsQuery request, CancellationToken cancellationToken) =>
        new LotList(await queries.ExpiringLotsAsync(Math.Clamp(request.Days, 0, 365), cancellationToken));

    public async Task<Result<LowStockList>> Handle(GetLowStockQuery request, CancellationToken cancellationToken) =>
        new LowStockList(await queries.LowStockAsync(cancellationToken));

    public async Task<Result<LowStockCount>> Handle(GetLowStockCountQuery request, CancellationToken cancellationToken) =>
        new LowStockCount(await queries.LowStockCountAsync(cancellationToken));

    public async Task<Result<ReservationList>> Handle(GetHeldReservationsQuery request, CancellationToken cancellationToken) =>
        new ReservationList(await queries.HeldReservationsAsync(cancellationToken));
}
