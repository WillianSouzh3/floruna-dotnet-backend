using System.Text.Json.Serialization;
using Floruna.Application.Catalog;
using Floruna.Domain.Inventory;

namespace Floruna.Application.Inventory;

// Formatos do Nest: /stock/:productId/movements → { movements }, /lots → { lots }...

public sealed record StockMovementResponse(
    string Id, string ProductId, string ProductName, string Type, int Delta, int ResultingStock,
    string Note, string By, string? OrderId, string CreatedAt)
{
    public static StockMovementResponse From(StockMovement m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(m.Id.ToString(), m.ProductId.ToString(), m.ProductName, ContractType(m.Type), m.Delta, m.ResultingStock,
            m.Note, m.By, m.OrderId?.ToString(), CatalogMapping.Iso(m.CreatedAt));
    }

    /// <summary><c>OpeningBalance</c> → <c>opening_balance</c>; os demais em minúsculas (<c>venda</c>, <c>entrada</c>...).</summary>
    public static string ContractType(StockMovementType type) =>
        type == StockMovementType.OpeningBalance ? "opening_balance" : type.ToString().ToLowerInvariant();
}

public sealed record StockLotResponse(
    string Id, string ProductId, string ProductName, int Quantity, int InitialQuantity, string? ExpiresAt,
    [property: JsonPropertyName("cost")] long? CostCents, string Note, string By, string CreatedAt);

public sealed record StockReservationResponse(
    string Id, string ProductId, int Quantity, string OwnerType, string OwnerId, string State, string ExpiresAt, string? SettledAt, string Note)
{
    public static StockReservationResponse From(StockReservation r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.Id.ToString(), r.ProductId.ToString(), r.Quantity, r.OwnerType.ToString().ToLowerInvariant(), r.OwnerId,
            r.State.ToString().ToLowerInvariant(), CatalogMapping.Iso(r.ExpiresAt), r.SettledAt is { } s ? CatalogMapping.Iso(s) : null, r.Note);
    }
}

/// <summary>Resposta das operações de estoque, como no Nest: <c>{ product, movement }</c>.</summary>
public sealed record StockOperationResponse(ProductResponse Product, StockMovementResponse Movement);

public sealed record MovementList(IReadOnlyList<StockMovementResponse> Movements);

public sealed record LotList(IReadOnlyList<StockLotResponse> Lots);

public sealed record LowStockList(IReadOnlyList<ProductResponse> Products);

public sealed record LowStockCount(int Count);

public sealed record ReservationList(IReadOnlyList<StockReservationResponse> StockReservations);

public sealed record ReservationEnvelope(StockReservationResponse StockReservation);

/// <summary>Leituras de estoque (lado Q do CQRS).</summary>
public interface IInventoryQueries
{
    Task<IReadOnlyList<StockMovementResponse>> MovementsAsync(Guid productId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StockLotResponse>> ActiveLotsAsync(Guid productId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StockLotResponse>> ExpiringLotsAsync(int days, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductResponse>> LowStockAsync(CancellationToken cancellationToken);

    Task<int> LowStockCountAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StockReservationResponse>> HeldReservationsAsync(CancellationToken cancellationToken);

    /// <summary>Reservas vencidas de TODAS as lojas (job de expiração): (loja, reserva).</summary>
    Task<IReadOnlyList<(Guid TenantId, Guid ReservationId)>> ExpiredHeldReservationsAsync(int batchSize, CancellationToken cancellationToken);
}
