using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Loyalty;
using Floruna.Domain.Loyalty;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Loyalty;

internal sealed class LoyaltyRepository(FlorunaDbContext db, ITenantContext tenantContext) : ILoyaltyRepository
{
    public Task<LoyaltyProgram?> GetProgramAsync(CancellationToken cancellationToken) =>
        db.LoyaltyPrograms.FirstOrDefaultAsync(cancellationToken);

    public void Add(LoyaltyProgram program) => db.LoyaltyPrograms.Add(program);

    public void Add(LoyaltyAccount account) => db.LoyaltyAccounts.Add(account);

    /// <summary>
    /// Trava a linha da conta antes de carregar: resgate no checkout, crédito na entrega e
    /// expiração do job passam todos por aqui e entram em FILA. Sem isso, dois checkouts
    /// simultâneos leriam o mesmo saldo e gastariam duas vezes.
    /// </summary>
    public async Task<LoyaltyAccount?> GetForUpdateAsync(string email, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var normalized = email.Trim().ToLowerInvariant();
        var tenant = tenantContext.RequiredTenantId.Value;
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM loyalty_accounts WHERE email = {normalized} AND tenant_id = {tenant} FOR UPDATE", cancellationToken);
        return await WithOpenLots().FirstOrDefaultAsync(a => a.Email == normalized, cancellationToken);
    }

    public async Task<LoyaltyAccount?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTransaction();
        var tenant = tenantContext.RequiredTenantId.Value;
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM loyalty_accounts WHERE id = {id} AND tenant_id = {tenant} FOR UPDATE", cancellationToken);
        return await WithOpenLots().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    /// <summary>
    /// Leitura SEM rastreamento, de propósito. O checkout lê a carteira para cotar e depois
    /// a trava com <see cref="GetForUpdateAsync"/> no mesmo DbContext. Se esta leitura
    /// ficasse rastreada, a consulta pós-trava devolveria a MESMA instância em memória
    /// (identity resolution do EF não sobrescreve entidade rastreada) com o saldo antigo:
    /// a trava existiria, mas o código leria dado velho e gastaria o saldo duas vezes.
    /// O teste de concorrência pegou exatamente isso.
    /// </summary>
    public Task<LoyaltyAccount?> FindAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return WithOpenLots().AsNoTracking().FirstOrDefaultAsync(a => a.Email == normalized, cancellationToken);
    }

    public Task<bool> EntryExistsAsync(string dedupeKey, CancellationToken cancellationToken) =>
        db.LoyaltyEntries.AnyAsync(e => e.DedupeKey == dedupeKey, cancellationToken);

    /// <summary>Insere as linhas novas; os lotes já existentes só têm o saldo restante atualizado.</summary>
    public void Track(LoyaltyAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        foreach (var entry in account.NewEntries.Where(e => db.Entry(e).State == EntityState.Detached))
        {
            db.LoyaltyEntries.Add(entry);
        }
    }

    private IQueryable<LoyaltyAccount> WithOpenLots() => db.LoyaltyAccounts.Include(a => a.ActiveCredits.Where(e => e.Remaining > 0));

    private void EnsureTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Trava de linha exige transação: use IUnitOfWork.ExecuteInTransactionAsync.");
        }
    }
}

internal sealed class LoyaltyQueries(FlorunaDbContext db) : ILoyaltyQueries
{
    public async Task<(IReadOnlyList<LoyaltyAccountResponse> Items, int Total)> AccountsAsync(int page, int limit, CancellationToken cancellationToken)
    {
        var query = db.LoyaltyAccounts.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(a => a.TotalSpentCents).ThenBy(a => a.Email)
            .Skip((page - 1) * limit).Take(limit).ToListAsync(cancellationToken);
        return ([.. items.Select(LoyaltyMapping.ToResponse)], total);
    }

    public async Task<long> OutstandingCashbackAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.LoyaltyEntries.Where(e => e.Remaining > 0 && (e.ExpiresAt == null || e.ExpiresAt > now)).SumAsync(e => (long?)e.Remaining, cancellationToken) ?? 0;

    public async Task<(IReadOnlyList<LoyaltyEntryResponse> Items, int Total)> StatementAsync(Guid accountId, int page, int limit, CancellationToken cancellationToken)
    {
        var query = db.LoyaltyEntries.AsNoTracking().Where(e => e.AccountId == accountId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Skip((page - 1) * limit).Take(limit).ToListAsync(cancellationToken);
        return ([.. items.Select(LoyaltyMapping.ToResponse)], total);
    }

    public async Task<ExpiringLot?> NextExpiringAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lot = await db.LoyaltyEntries.AsNoTracking()
            .Where(e => e.AccountId == accountId && e.Remaining > 0 && e.ExpiresAt > now)
            .OrderBy(e => e.ExpiresAt).Select(e => new { e.Remaining, e.ExpiresAt }).FirstOrDefaultAsync(cancellationToken);
        return lot is null ? null : new ExpiringLot(lot.Remaining, CatalogMapping.Iso(lot.ExpiresAt!.Value));
    }

    public async Task<IReadOnlyList<(Guid TenantId, Guid AccountId)>> AccountsWithExpiredCreditsAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        // Job (todas as lojas): índice parcial ix_loyalty_entries_expiring.
        var rows = await db.LoyaltyEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Remaining > 0 && e.ExpiresAt <= now)
            .Select(e => new { e.TenantId, e.AccountId }).Distinct().Take(batchSize).ToListAsync(cancellationToken);
        return [.. rows.Select(r => (r.TenantId.Value, r.AccountId))];
    }
}
