using Floruna.Application.Abstractions;
using Floruna.Domain.Delivery;
using Floruna.Domain.Ordering;
using Floruna.Domain.Promotions;
using Floruna.Infrastructure.Persistence;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Ordering;

internal sealed class CouponRepository(FlorunaDbContext db) : ICouponRepository
{
    public void Add(Coupon coupon) => db.Coupons.Add(coupon);

    public void Add(CouponRedemption redemption) => db.CouponRedemptions.Add(redemption);

    public void Remove(Coupon coupon) => db.Coupons.Remove(coupon);

    public Task<Coupon?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Coupons.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<Coupon?> FindActiveByCodeAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalized && c.IsActive, cancellationToken);
    }

    public Task<int> CountRedemptionsAsync(Guid couponId, string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return db.CouponRedemptions.CountAsync(r => r.CouponId == couponId && r.Email == normalized, cancellationToken);
    }

    /// <summary>
    /// <c>UPDATE coupons SET used_count = used_count + 1 WHERE ... AND used_count &lt; max_uses</c>:
    /// condição e incremento numa escrita só. A última unidade de um cupom disputada por dois
    /// checkouts: o banco deixa um passar e o outro recebe "0 linhas".
    /// </summary>
    public async Task<bool> TryClaimUseAsync(Guid couponId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Coupons
            .Where(c => c.Id == couponId && c.IsActive && (c.ExpiresAt == null || c.ExpiresAt > now) && (c.MaxUses == null || c.UsedCount < c.MaxUses))
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.UsedCount, c => c.UsedCount + 1), cancellationToken) == 1;

    public async Task ReleaseUseAsync(Guid couponId, Guid orderId, CancellationToken cancellationToken)
    {
        var removed = await db.CouponRedemptions.Where(r => r.CouponId == couponId && r.OrderId == orderId).ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            await db.Coupons.Where(c => c.Id == couponId && c.UsedCount > 0)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.UsedCount, c => c.UsedCount - 1), cancellationToken);
        }
    }
}

internal sealed class DeliveryRepository(FlorunaDbContext db, ITenantContext tenantContext) : IDeliveryRepository
{
    public void Add(DeliveryZone zone) => db.DeliveryZones.Add(zone);

    public void Add(CapacityRule rule) => db.CapacityRules.Add(rule);

    public void Remove(DeliveryZone zone) => db.DeliveryZones.Remove(zone);

    public void Remove(CapacityRule rule) => db.CapacityRules.Remove(rule);

    public Task<DeliveryZone?> GetZoneAsync(Guid id, CancellationToken cancellationToken) =>
        db.DeliveryZones.FirstOrDefaultAsync(z => z.Id == id, cancellationToken);

    public Task<CapacityRule?> GetRuleAsync(Guid id, CancellationToken cancellationToken) =>
        db.CapacityRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<DeliveryZone?> FindZoneByZipAsync(int zip, CancellationToken cancellationToken) =>
        db.DeliveryZones.AsNoTracking()
            .Where(z => z.IsActive && z.ZipStart <= zip && z.ZipEnd >= zip)
            .OrderBy(z => z.FeeCents)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> AnyZoneAsync(CancellationToken cancellationToken) => db.DeliveryZones.AnyAsync(z => z.IsActive, cancellationToken);

    public async Task<IReadOnlyList<CapacityRule>> RulesForAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var weekday = (int)date.DayOfWeek;
        return await db.CapacityRules.AsNoTracking()
            .Where(r => r.IsActive && (r.Date == date || (r.Kind == CapacityRuleKind.Recurring && r.Weekday == weekday)))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> UsedAsync(DateOnly date, DeliveryWindow window, Guid? zoneId, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.RequiredTenantId.Value;
        var key = zoneId ?? Guid.Empty;
        return await db.DeliverySlotUsage.AsNoTracking()
            .Where(u => u.TenantId == tenant && u.Date == date && u.Window == window && u.ZoneKey == key)
            .Select(u => u.Used)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>UPSERT condicional: insere com 1 ou soma 1 SÓ se ainda couber (<c>used &lt; capacity</c>).</summary>
    public async Task<bool> TryOccupyAsync(DateOnly date, DeliveryWindow window, Guid? zoneId, int? capacity, CancellationToken cancellationToken)
    {
        if (capacity is <= 0)
        {
            return false;
        }

        var tenant = tenantContext.RequiredTenantId.Value;
        var key = zoneId ?? Guid.Empty;
        var windowName = window.ToString();
        var rows = await db.Database.SqlQuery<int>($"""
            INSERT INTO delivery_slot_usage (tenant_id, date, "window", zone_key, used)
            VALUES ({tenant}, {date}, {windowName}, {key}, 1)
            ON CONFLICT (tenant_id, date, "window", zone_key) DO UPDATE SET used = delivery_slot_usage.used + 1
            WHERE {capacity}::int IS NULL OR delivery_slot_usage.used < {capacity}::int
            RETURNING used AS "Value"
            """).ToListAsync(cancellationToken);
        return rows.Count == 1;
    }

    public Task FreeAsync(DateOnly date, DeliveryWindow window, Guid? zoneId, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.RequiredTenantId.Value;
        var key = zoneId ?? Guid.Empty;
        return db.DeliverySlotUsage
            .Where(u => u.TenantId == tenant && u.Date == date && u.Window == window && u.ZoneKey == key && u.Used > 0)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.Used, u => u.Used - 1), cancellationToken);
    }
}

internal sealed class OrderRepository(FlorunaDbContext db) : IOrderRepository
{
    public void Add(Order order) => db.Orders.Add(order);

    public Task<Order?> GetAsync(OrderId id, CancellationToken cancellationToken) =>
        db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public Task<Order?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken) =>
        db.Orders.FirstOrDefaultAsync(o => o.IdempotencyKey == key, cancellationToken);

    /// <summary>Consulta de PLATAFORMA (job): pendentes vencidos de todas as lojas.</summary>
    public async Task<IReadOnlyList<(TenantId TenantId, OrderId OrderId)>> AbandonedAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var rows = await db.Orders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.Status == OrderStatus.Pendente && o.PaymentDeadline <= now)
            .OrderBy(o => o.PaymentDeadline)
            .Take(batchSize)
            .Select(o => new { o.TenantId, o.Id })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => (r.TenantId, r.Id))];
    }
}
