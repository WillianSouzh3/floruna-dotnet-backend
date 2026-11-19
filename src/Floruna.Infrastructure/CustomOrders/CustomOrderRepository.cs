using Floruna.Application.Abstractions;
using Floruna.Application.CustomOrders;
using Floruna.Domain.CustomOrders;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.CustomOrders;

internal sealed class CustomOrderRepository(FlorunaDbContext db, ITenantContext tenantContext) : ICustomOrderRepository
{
    public void Add(CustomOrderRequest request) => db.CustomOrderRequests.Add(request);

    public void Add(CustomQuote quote) => db.CustomQuotes.Add(quote);

    public Task<CustomOrderRequest?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.CustomOrderRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<CustomOrderRequest?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Trava de linha exige transação: use IUnitOfWork.ExecuteInTransactionAsync.");
        }

        var tenant = tenantContext.RequiredTenantId.Value;
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM custom_order_requests WHERE id = {id} AND tenant_id = {tenant} FOR UPDATE", cancellationToken);

        // Dado fresco depois da trava: descarta a solicitação e os orçamentos dela que estavam rastreados.
        foreach (var stale in db.CustomOrderRequests.Local.Where(r => r.Id == id).ToList())
        {
            db.Entry(stale).State = EntityState.Detached;
        }

        foreach (var stale in db.CustomQuotes.Local.Where(q => q.RequestId == id).ToList())
        {
            db.Entry(stale).State = EntityState.Detached;
        }

        return await db.CustomOrderRequests.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public Task<CustomQuote?> GetQuoteAsync(Guid id, CancellationToken cancellationToken) =>
        db.CustomQuotes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

    public async Task<(CustomOrderRequest Request, CustomQuote Quote)?> LockQuoteAsync(Guid quoteId, CancellationToken cancellationToken)
    {
        var requestId = await db.CustomQuotes.AsNoTracking().Where(q => q.Id == quoteId).Select(q => (Guid?)q.RequestId).FirstOrDefaultAsync(cancellationToken);
        if (requestId is null || await GetForUpdateAsync(requestId.Value, cancellationToken) is not { } request)
        {
            return null;
        }

        var quote = await db.CustomQuotes.FirstAsync(q => q.Id == quoteId, cancellationToken);
        return (request, quote);
    }

    public async Task<IReadOnlyList<CustomQuote>> QuotesOfAsync(Guid requestId, CancellationToken cancellationToken) =>
        await db.CustomQuotes.Where(q => q.RequestId == requestId).OrderBy(q => q.Version).ToListAsync(cancellationToken);
}

internal sealed class CustomOrderQueries(FlorunaDbContext db) : ICustomOrderQueries
{
    public async Task<(IReadOnlyList<CustomOrderRequestResponse> Items, int Total)> ListAsync(int page, int limit, string? status, CancellationToken cancellationToken)
    {
        var query = db.CustomOrderRequests.AsNoTracking().Where(r => status == null || r.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * limit).Take(limit).ToListAsync(cancellationToken);
        return ([.. items.Select(CustomOrderRequestResponse.From)], total);
    }

    public async Task<IReadOnlyList<(Guid TenantId, Guid QuoteId)>> DueQuotesAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        // Job (todas as lojas): índice parcial ix_custom_quotes_published_valid_until.
        var rows = await db.CustomQuotes.IgnoreQueryFilters().AsNoTracking()
            .Where(q => q.State == QuoteState.Published && q.ValidUntil <= now)
            .OrderBy(q => q.ValidUntil).Take(batchSize)
            .Select(q => new { q.TenantId, q.Id }).ToListAsync(cancellationToken);
        return [.. rows.Select(r => (r.TenantId.Value, r.Id))];
    }
}
