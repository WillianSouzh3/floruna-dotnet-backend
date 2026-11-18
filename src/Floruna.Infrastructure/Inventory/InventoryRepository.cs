using Floruna.Application.Abstractions;
using Floruna.Domain.Catalog;
using Floruna.Domain.Inventory;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Inventory;

internal sealed class InventoryRepository(FlorunaDbContext db, ITenantContext tenantContext) : IInventoryRepository
{
    public void Add(StockItem item) => db.StockItems.Add(item);

    public void Add(StockMovement movement) => db.StockMovements.Add(movement);

    public void Add(StockReservation reservation) => db.StockReservations.Add(reservation);

    /// <summary>
    /// Trava PESSIMISTA da linha (<c>FOR UPDATE</c>) e só depois carrega o agregado.
    /// Quem chegar em seguida para o mesmo produto ESPERA o commit e lê o saldo já
    /// atualizado. Por que não concorrência otimista? Num produto disputado (Dia das
    /// Mães), a otimista gera tempestade de conflitos e retentativas; a fila no banco
    /// é previsível. O custo é segurar a trava pelo tempo da transação: por isso a
    /// transação é curta e nunca faz chamada externa (gateway, e-mail) lá dentro.
    /// </summary>
    public async Task<StockItem?> GetForUpdateAsync(ProductId productId, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var tenant = tenantContext.RequiredTenantId.Value;
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM stock_items WHERE product_id = {productId.Value} AND tenant_id = {tenant} FOR UPDATE", cancellationToken);

        return await db.StockItems
            .Include(i => i.Lots.Where(l => l.Remaining > 0)) // lotes esgotados ficam no banco, fora da memória
            .FirstOrDefaultAsync(i => i.Id == productId, cancellationToken);
    }

    public async Task<StockReservation?> GetReservationAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var tenant = tenantContext.RequiredTenantId.Value;
        // Trava a reserva também: duas liberações simultâneas (painel + job) não devolvem em dobro.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM stock_reservations WHERE id = {id} AND tenant_id = {tenant} FOR UPDATE", cancellationToken);
        return await db.StockReservations.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public Task<StockReservation?> FindReservationByKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.StockReservations.FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<StockReservation?> GetReservationByKeyForUpdateAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var tenant = tenantContext.RequiredTenantId.Value;
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM stock_reservations WHERE idempotency_key = {idempotencyKey} AND tenant_id = {tenant} FOR UPDATE", cancellationToken);
        return await db.StockReservations.FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public Task<bool> MovementExistsAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.StockMovements.AnyAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);

    private void EnsureTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Trava de linha exige transação: use IUnitOfWork.ExecuteInTransactionAsync.");
        }
    }
}
