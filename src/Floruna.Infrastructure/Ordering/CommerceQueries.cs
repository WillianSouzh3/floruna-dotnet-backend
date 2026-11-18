using Floruna.Application.Catalog;
using Floruna.Application.Delivery;
using Floruna.Application.Ordering;
using Floruna.Application.Outbox;
using Floruna.Application.Promotions;
using Floruna.Domain.Ordering;
using Floruna.Infrastructure.Outbox;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Ordering;

internal sealed class CommerceQueries(FlorunaDbContext db) : ICouponQueries, IDeliveryQueries, IOrderQueries
{
    public async Task<IReadOnlyList<CouponResponse>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Coupons.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync(cancellationToken)).Select(CouponResponse.From).ToList();

    public async Task<IReadOnlyList<ZoneResponse>> ZonesAsync(CancellationToken cancellationToken) =>
        (await db.DeliveryZones.AsNoTracking().OrderBy(z => z.ZipStart).ToListAsync(cancellationToken)).Select(ZoneResponse.From).ToList();

    public async Task<IReadOnlyList<RuleResponse>> RulesAsync(CancellationToken cancellationToken) =>
        (await db.CapacityRules.AsNoTracking().OrderBy(r => r.Kind).ThenBy(r => r.Date).ThenBy(r => r.Weekday).ToListAsync(cancellationToken))
            .Select(RuleResponse.From).ToList();

    public async Task<OrderPage> ListAsync(OrderListFilter filter, Guid? customerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = db.Orders.AsNoTracking();
        if (customerId is { } customer)
        {
            query = query.Where(o => o.CustomerId == customer);
        }

        if (OrderStatusContract.Parse(filter.Status) is { } status)
        {
            query = query.Where(o => o.Status == status);
        }

        if (filter.MinTotal is { } min)
        {
            query = query.Where(o => o.TotalCents >= min);
        }

        if (filter.MaxTotal is { } max)
        {
            query = query.Where(o => o.TotalCents <= max);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Busca dentro do jsonb do cliente (nome, telefone, e-mail).
            var pattern = $"%{filter.Search.Trim()}%";
            query = query.Where(o => EF.Functions.ILike(o.Customer.Name, pattern)
                || EF.Functions.ILike(o.Customer.Phone, pattern)
                || (o.Customer.Email != null && EF.Functions.ILike(o.Customer.Email, pattern)));
        }

        var total = await query.CountAsync(cancellationToken);
        var orders = await query.OrderByDescending(o => o.CreatedAt)
            .Skip((filter.Page - 1) * filter.Limit).Take(filter.Limit)
            .ToListAsync(cancellationToken);
        return new OrderPage(
            [.. orders.Select(o => OrderResponse.From(o, internalView: customerId is null))],
            total, filter.Page, filter.Limit, (int)Math.Ceiling(total / (double)filter.Limit));
    }
}

internal sealed class OutboxAdmin(FlorunaDbContext db) : IOutboxAdmin
{
    public async Task<IReadOnlyList<OutboxMessageResponse>> ListAsync(string state, int limit, CancellationToken cancellationToken)
    {
        var parsed = Enum.TryParse<OutboxState>(state, ignoreCase: true, out var s) ? s : OutboxState.Dead;
        var rows = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.State == parsed)
            .OrderByDescending(m => m.OccurredAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);
        return [.. rows.Select(m => new OutboxMessageResponse(m.Id.ToString(), m.TenantId.ToString(), m.Type.Split('.')[^1],
            m.State.ToString().ToLowerInvariant(), m.Attempts, m.LastError, CatalogMapping.Iso(m.OccurredAt)))];
    }

    public async Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken) =>
        await db.OutboxMessages
            .Where(m => m.Id == id && m.State == OutboxState.Dead)
            .ExecuteUpdateAsync(set => set
                .SetProperty(m => m.State, OutboxState.Pending)
                .SetProperty(m => m.Attempts, 0)
                .SetProperty(m => m.NextAttemptAt, m => m.OccurredAt), cancellationToken) == 1;
}
