using Floruna.Application.Catalog;
using Floruna.Application.Inventory;
using Floruna.Domain.Catalog;
using Floruna.Domain.Inventory;
using Floruna.Infrastructure.Catalog;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Inventory;

internal sealed class InventoryQueries(FlorunaDbContext db, CatalogQueries catalogQueries, TimeProvider clock) : IInventoryQueries
{
    public async Task<IReadOnlyList<StockMovementResponse>> MovementsAsync(Guid productId, CancellationToken cancellationToken)
    {
        var id = new ProductId(productId);
        var movements = await db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == id)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(500)
            .ToListAsync(cancellationToken);
        return [.. movements.Select(StockMovementResponse.From)];
    }

    public Task<IReadOnlyList<StockLotResponse>> ActiveLotsAsync(Guid productId, CancellationToken cancellationToken)
    {
        var id = new ProductId(productId);
        return LotsAsync(db.StockLots.Where(l => l.ProductId == id && l.Remaining > 0), cancellationToken);
    }

    /// <summary>Lotes com saldo que vencem nos próximos <paramref name="days"/> dias (inclui os já vencidos).</summary>
    public Task<IReadOnlyList<StockLotResponse>> ExpiringLotsAsync(int days, CancellationToken cancellationToken)
    {
        var limit = clock.GetUtcNow().AddDays(days);
        return LotsAsync(db.StockLots.Where(l => l.Remaining > 0 && l.ExpiresAt != null && l.ExpiresAt <= limit), cancellationToken);
    }

    /// <summary>Produtos cujo vendável está no limite de alerta ou abaixo (do mais crítico ao menos).</summary>
    public async Task<IReadOnlyList<ProductResponse>> LowStockAsync(CancellationToken cancellationToken)
    {
        var products = await LowStockProducts().ToListAsync(cancellationToken);
        var mapped = await catalogQueries.MapAsync(products, includeCost: true, cancellationToken);
        return [.. mapped.OrderBy(p => p.Availability.Available)];
    }

    public Task<int> LowStockCountAsync(CancellationToken cancellationToken) => LowStockProducts().CountAsync(cancellationToken);

    public async Task<IReadOnlyList<StockReservationResponse>> HeldReservationsAsync(CancellationToken cancellationToken)
    {
        var held = await db.StockReservations.AsNoTracking()
            .Where(r => r.State == ReservationState.Held)
            .OrderBy(r => r.ExpiresAt)
            .ToListAsync(cancellationToken);
        return [.. held.Select(StockReservationResponse.From)];
    }

    /// <summary>
    /// Consulta de PLATAFORMA (todas as lojas): escape consciente do filtro de loja.
    /// O job abre o contexto de cada loja antes de alterar qualquer coisa.
    /// </summary>
    public async Task<IReadOnlyList<(Guid TenantId, Guid ReservationId)>> ExpiredHeldReservationsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expired = await db.StockReservations.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.State == ReservationState.Held && r.ExpiresAt <= now)
            .OrderBy(r => r.ExpiresAt)
            .Take(batchSize)
            .Select(r => new { r.TenantId, r.Id })
            .ToListAsync(cancellationToken);
        return [.. expired.Select(r => (r.TenantId.Value, r.Id))];
    }

    private IQueryable<Product> LowStockProducts()
    {
        var now = clock.GetUtcNow();
        return db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .Where(p => (db.StockLots.Where(l => l.ProductId == p.Id && l.Remaining > 0 && (l.ExpiresAt == null || l.ExpiresAt > now)).Sum(l => (int?)l.Remaining) ?? 0)
                - (db.StockItems.Where(i => i.Id == p.Id).Select(i => (int?)i.Reserved).FirstOrDefault() ?? 0)
                <= p.LowStockThreshold);
    }

    private async Task<IReadOnlyList<StockLotResponse>> LotsAsync(IQueryable<StockLot> lots, CancellationToken cancellationToken)
    {
        var rows = await lots.AsNoTracking()
            .OrderBy(l => l.ExpiresAt == null).ThenBy(l => l.ExpiresAt).ThenBy(l => l.ReceivedAt) // ordem FEFO
            .Join(db.Products, l => l.ProductId, p => p.Id, (l, p) => new { Lot = l, p.Name })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new StockLotResponse(
            r.Lot.Id.ToString(), r.Lot.ProductId.ToString(), r.Name, r.Lot.Remaining, r.Lot.InitialQuantity,
            r.Lot.ExpiresAt is { } e ? CatalogMapping.Iso(e) : null, r.Lot.UnitCostCents, r.Lot.Note, r.Lot.ReceivedBy,
            CatalogMapping.Iso(r.Lot.ReceivedAt)))];
    }
}
