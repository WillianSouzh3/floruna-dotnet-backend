using Floruna.Domain.Audit;
using Floruna.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Persistence.Repositories;

internal sealed class SingleUseTokenRepository(FlorunaDbContext db) : ISingleUseTokenRepository
{
    public void Add(SingleUseToken token) => db.SingleUseTokens.Add(token);

    public Task RevokePendingAsync(StaffUserId userId, SingleUseTokenPurpose purpose, CancellationToken cancellationToken) =>
        db.SingleUseTokens
            .Where(t => t.UserId == userId && t.Purpose == purpose && t.ConsumedAt == null)
            .ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// Compare-and-swap em duas etapas: acha a credencial e faz
    /// <c>UPDATE ... SET consumed_at = now WHERE id = @id AND consumed_at IS NULL</c>.
    /// Se duas requisições chegarem juntas, o banco só deixa UMA atualizar a linha;
    /// a outra vê "0 linhas afetadas" e perde. Sem lock explícito, sem transação longa.
    /// </summary>
    public async Task<SingleUseToken?> TryConsumeAsync(SingleUseTokenPurpose purpose, string secretHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = await db.SingleUseTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Purpose == purpose && t.SecretHash == secretHash && t.ConsumedAt == null && t.ExpiresAt > now, cancellationToken);
        if (token is null)
        {
            return null;
        }

        var won = await db.SingleUseTokens
            .Where(t => t.Id == token.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.ConsumedAt, now), cancellationToken);
        return won == 1 ? token : null;
    }
}

internal sealed class AuditRepository(FlorunaDbContext db) : IAuditRepository
{
    public void Add(AuditEntry entry) => db.AuditEntries.Add(entry);
}
