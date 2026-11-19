using Floruna.Application.Abstractions;
using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Payments;

internal sealed class RefundRepository(FlorunaDbContext db, ITenantContext tenantContext) : IRefundRepository
{
    public void Add(Refund refund) => db.Refunds.Add(refund);

    public Task<Refund?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Refunds.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<Refund?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.Refunds.FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<Refund?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var tenant = tenantContext.RequiredTenantId.Value;
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM refunds WHERE id = {id} AND tenant_id = {tenant} FOR UPDATE", cancellationToken);
        Forget(db.Refunds.Local.FirstOrDefault(r => r.Id == id));
        return await db.Refunds.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Refund>> ByIntentAsync(Guid intentId, CancellationToken cancellationToken) =>
        await db.Refunds.AsNoTracking().Where(r => r.IntentId == intentId).OrderBy(r => r.CreatedAt).ToListAsync(cancellationToken);

    /// <summary>
    /// Trava a intenção e devolve o estado ATUAL do banco. Se uma cópia já estava rastreada neste
    /// DbContext (alguém leu antes para checar o status), ela é descartada antes da consulta:
    /// senão o EF devolveria a cópia velha e a trava não protegeria nada (lição da ADR 0007).
    /// </summary>
    public async Task<PaymentIntent?> GetIntentForUpdateAsync(Guid intentId, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var tenant = tenantContext.RequiredTenantId.Value;
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM payment_intents WHERE id = {intentId} AND tenant_id = {tenant} FOR UPDATE", cancellationToken);
        Forget(db.PaymentIntents.Local.FirstOrDefault(i => i.Id == intentId));
        return await db.PaymentIntents.FirstOrDefaultAsync(i => i.Id == intentId, cancellationToken);
    }

    public async Task<bool> TryClaimAsync(Guid refundId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lease = now.AddSeconds(60);
        var claimed = await db.Refunds
            .Where(r => r.Id == refundId && r.Status == RefundStatus.Pending && r.Attempts < Refund.MaxAttempts
                        && r.NextAttemptAt <= now && (r.LeaseUntil == null || r.LeaseUntil < now))
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.LeaseUntil, lease), cancellationToken);
        if (claimed == 1)
        {
            Forget(db.Refunds.Local.FirstOrDefault(r => r.Id == refundId)); // o UPDATE direto não passa pelo rastreador
        }

        return claimed == 1;
    }

    private void Forget(object? entity)
    {
        if (entity is not null)
        {
            db.Entry(entity).State = EntityState.Detached;
        }
    }

    private void EnsureTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Trava de linha exige transação: use IUnitOfWork.ExecuteInTransactionAsync.");
        }
    }
}

/// <summary>Reembolsos pendentes de TODAS as lojas, para o job de retentativa.</summary>
internal sealed class RefundQueries(FlorunaDbContext db) : Application.Payments.IRefundQueries
{
    public async Task<IReadOnlyList<(Guid TenantId, Guid RefundId)>> DueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var rows = await db.Refunds.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Status == RefundStatus.Pending && r.Attempts < Refund.MaxAttempts && r.NextAttemptAt <= now
                        && (r.LeaseUntil == null || r.LeaseUntil < now))
            .OrderBy(r => r.NextAttemptAt).Take(batchSize)
            .Select(r => new { r.TenantId, r.Id }).ToListAsync(cancellationToken);
        return [.. rows.Select(r => (r.TenantId.Value, r.Id))];
    }
}
