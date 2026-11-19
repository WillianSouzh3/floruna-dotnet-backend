using Floruna.Application.Abstractions;
using Floruna.Application.Substitutions;
using Floruna.Domain.Substitutions;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Substitutions;

internal sealed class SubstitutionRepository(FlorunaDbContext db, ITenantContext tenantContext) : ISubstitutionRepository
{
    public Task<SubstitutionPolicy?> GetPolicyAsync(CancellationToken cancellationToken) => db.SubstitutionPolicies.FirstOrDefaultAsync(cancellationToken);

    public void Add(SubstitutionPolicy policy) => db.SubstitutionPolicies.Add(policy);

    public void Add(SubstitutionProposal proposal) => db.SubstitutionProposals.Add(proposal);

    public Task<SubstitutionProposal?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.SubstitutionProposals.AsNoTracking().FirstOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey, cancellationToken);

    public Task<SubstitutionProposal?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.SubstitutionProposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<SubstitutionProposal?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Trava de linha exige transação: use IUnitOfWork.ExecuteInTransactionAsync.");
        }

        var tenant = tenantContext.RequiredTenantId.Value;
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM substitution_proposals WHERE id = {id} AND tenant_id = {tenant} FOR UPDATE", cancellationToken);
        foreach (var stale in db.SubstitutionProposals.Local.Where(p => p.Id == id).ToList())
        {
            db.Entry(stale).State = EntityState.Detached; // dado fresco depois da trava (ADR 0007)
        }

        return await db.SubstitutionProposals.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<SubstitutionProposal>> ByOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.SubstitutionProposals.AsNoTracking().Where(p => p.OrderId == orderId).OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
}

internal sealed class SubstitutionQueries(FlorunaDbContext db) : ISubstitutionQueries
{
    public async Task<IReadOnlyList<(Guid TenantId, Guid ProposalId)>> DueAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var rows = await db.SubstitutionProposals.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.State == ProposalState.Proposed && p.ExpiresAt <= now)
            .OrderBy(p => p.ExpiresAt).Take(batchSize)
            .Select(p => new { p.TenantId, p.Id }).ToListAsync(cancellationToken);
        return [.. rows.Select(r => (r.TenantId.Value, r.Id))];
    }
}
