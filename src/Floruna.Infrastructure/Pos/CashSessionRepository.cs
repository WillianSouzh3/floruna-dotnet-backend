using Floruna.Application.Abstractions;
using Floruna.Application.Pos;
using Floruna.Domain.Pos;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Pos;

internal sealed class CashSessionRepository(FlorunaDbContext db, ITenantContext tenantContext) : ICashSessionRepository
{
    public void Add(CashSession session) => db.CashSessions.Add(session);

    public async Task<CashSession?> GetOpenForUpdateAsync(Guid operatorId, bool forSale, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Trava de linha exige transação: use IUnitOfWork.ExecuteInTransactionAsync.");
        }

        var tenant = tenantContext.RequiredTenantId.Value;
        // Se o fechamento for confirmado enquanto a venda espera, o PostgreSQL reavalia o WHERE na
        // versão nova da linha (status já não é 'Open') e a venda segue sem caixa: nunca entra num caixa fechado.
        if (forSale)
        {
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM cash_sessions WHERE operator_id = {operatorId} AND tenant_id = {tenant} AND status = 'Open' FOR SHARE", cancellationToken);
        }
        else
        {
            await db.Database.ExecuteSqlAsync(
                $"SELECT 1 FROM cash_sessions WHERE operator_id = {operatorId} AND tenant_id = {tenant} AND status = 'Open' FOR UPDATE", cancellationToken);
        }

        foreach (var stale in db.CashSessions.Local.Where(s => s.OperatorId == operatorId).ToList())
        {
            db.Entry(stale).State = EntityState.Detached; // dado sempre fresco depois da trava (ADR 0007)
        }

        return await db.CashSessions.FirstOrDefaultAsync(s => s.OperatorId == operatorId && s.Status == CashSessionStatus.Open, cancellationToken);
    }

    public Task<CashSession?> FindOpenAsync(Guid operatorId, CancellationToken cancellationToken) =>
        db.CashSessions.AsNoTracking().FirstOrDefaultAsync(s => s.OperatorId == operatorId && s.Status == CashSessionStatus.Open, cancellationToken);

    public async Task<long> CashSalesAsync(Guid sessionId, CancellationToken cancellationToken) =>
        await db.Orders.Where(o => o.CashSessionId == sessionId).SumAsync(o => (long?)o.CashReceivedCents, cancellationToken) ?? 0;
}

internal sealed class CashSessionQueries(FlorunaDbContext db) : ICashSessionQueries
{
    public async Task<(IReadOnlyList<CashSessionResponse> Items, int Total)> ClosedAsync(int page, int limit, CancellationToken cancellationToken)
    {
        var query = db.CashSessions.AsNoTracking().Where(s => s.Status == CashSessionStatus.Closed);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(s => s.ClosedAt).Skip((page - 1) * limit).Take(limit).ToListAsync(cancellationToken);
        return ([.. items.Select(s => CashSessionResponse.From(s, null))], total);
    }
}
