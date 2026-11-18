using System.Security.Cryptography;
using System.Text;
using Floruna.Application.Abstractions;
using Floruna.Domain.Identity;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Floruna.Infrastructure.Security;

/// <summary>Contador de falhas de login por chave (conta ou conta+IP).</summary>
internal sealed class LoginAttemptBucket
{
    public string Key { get; set; } = string.Empty;

    /// <summary>Chave da CONTA: login certo limpa todos os contadores dela de uma vez.</summary>
    public string GroupKey { get; set; } = string.Empty;

    public int Failures { get; set; }

    public DateTimeOffset WindowStartedAt { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>
/// Implementa o bloqueio progressivo com UPSERT atômico do PostgreSQL
/// (<c>INSERT ... ON CONFLICT DO UPDATE ... RETURNING</c>): duas falhas
/// simultâneas nunca contam como uma só.
/// </summary>
/// <remarks>
/// As chaves são HMAC de <c>loja|escopo|email[|ip]</c>: a tabela não guarda
/// e-mail nem IP em claro (LGPD), e um vazamento dela não revela quem tentou.
/// </remarks>
internal sealed class LoginAttemptGuard(FlorunaDbContext db, ITenantContext tenantContext, IOptions<JwtOptions> jwt, TimeProvider clock)
    : ILoginAttemptGuard
{
    public async Task<TimeSpan> RemainingLockAsync(LoginScope scope, string email, string? clientIp, CancellationToken cancellationToken)
    {
        var (account, accountIp) = Keys(scope, email, clientIp);
        var now = clock.GetUtcNow();
        var lockedUntil = await db.LoginAttempts.AsNoTracking()
            .Where(b => (b.Key == account || b.Key == accountIp) && b.LockedUntil > now)
            .MaxAsync(b => b.LockedUntil, cancellationToken);
        return lockedUntil is { } until ? until - now : TimeSpan.Zero;
    }

    public async Task RecordFailureAsync(LoginScope scope, string email, string? clientIp, CancellationToken cancellationToken)
    {
        var (account, accountIp) = Keys(scope, email, clientIp);
        await IncrementAsync(account, account, LoginLockoutPolicy.LockForAccount, cancellationToken);
        await IncrementAsync(accountIp, account, LoginLockoutPolicy.LockForAccountAndIp, cancellationToken);
    }

    public Task ClearAsync(LoginScope scope, string email, CancellationToken cancellationToken)
    {
        var (account, _) = Keys(scope, email, null);
        return db.LoginAttempts.Where(b => b.GroupKey == account).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task IncrementAsync(string key, string groupKey, Func<int, TimeSpan> policy, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var windowStart = now - LoginLockoutPolicy.Window;

        // Janela de 24 h: falha antiga demais recomeça a contagem do zero.
        var failures = (await db.Database.SqlQuery<int>($"""
            INSERT INTO login_attempts (key, group_key, failures, window_started_at, locked_until)
            VALUES ({key}, {groupKey}, 1, {now}, NULL)
            ON CONFLICT (key) DO UPDATE SET
                failures = CASE WHEN login_attempts.window_started_at > {windowStart} THEN login_attempts.failures + 1 ELSE 1 END,
                window_started_at = CASE WHEN login_attempts.window_started_at > {windowStart} THEN login_attempts.window_started_at ELSE {now} END
            RETURNING failures AS "Value"
            """).ToListAsync(cancellationToken)).Single();

        var lockFor = policy(failures);
        if (lockFor > TimeSpan.Zero)
        {
            var until = now + lockFor;
            await db.Database.ExecuteSqlAsync($"""
                UPDATE login_attempts SET locked_until = GREATEST(COALESCE(locked_until, {until}), {until}) WHERE key = {key}
                """, cancellationToken);
        }
    }

    private (string Account, string AccountIp) Keys(LoginScope scope, string email, string? clientIp)
    {
        var tenant = tenantContext.Tenant?.Id.ToString() ?? "platform";
        var account = $"{tenant}|{scope}|{email.Trim().ToLowerInvariant()}";
        return (Digest(account), Digest($"{account}|{clientIp ?? "unknown"}"));
    }

    private string Digest(string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwt.Value.Secret), Encoding.UTF8.GetBytes(value)));
}

/// <summary>Hashes de credenciais de uso único.</summary>
internal sealed class CredentialHasher(IOptions<JwtOptions> jwt) : ICredentialHasher
{
    public string HashOpaque(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    public string HashOneTimeCode(StaffUserId userId, string code) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(jwt.Value.Secret), Encoding.UTF8.GetBytes($"{userId}:{code}")));
}
