using Floruna.Domain.Identity;

namespace Floruna.Application.Abstractions;

public enum LoginScope
{
    Staff = 0,
    Customer = 1,
}

/// <summary>
/// Bloqueio progressivo de login por conta e por conta+IP (regras em
/// <see cref="LoginLockoutPolicy"/>). Complementa o rate limit por IP:
/// o rate limit freia rajadas; o bloqueio freia a adivinhação lenta de senha.
/// </summary>
public interface ILoginAttemptGuard
{
    /// <summary>Quanto falta para desbloquear (zero = liberado).</summary>
    Task<TimeSpan> RemainingLockAsync(LoginScope scope, string email, string? clientIp, CancellationToken cancellationToken);

    Task RecordFailureAsync(LoginScope scope, string email, string? clientIp, CancellationToken cancellationToken);

    Task ClearAsync(LoginScope scope, string email, CancellationToken cancellationToken);
}

/// <summary>Hashes de segredos de uso único (links e códigos). Nunca se guarda o segredo em si.</summary>
public interface ICredentialHasher
{
    /// <summary>SHA-256 de um token aleatório longo (link de reset, grant de suporte).</summary>
    string HashOpaque(string token);

    /// <summary>HMAC do código de 6 dígitos com o segredo do servidor (o código sozinho é fácil de adivinhar).</summary>
    string HashOneTimeCode(StaffUserId userId, string code);
}

public sealed record EmailMessage(string To, string Subject, string TextBody);

/// <summary>Envio de e-mail. A implementação enfileira e envia em segundo plano (não segura a requisição).</summary>
public interface IEmailSender
{
    bool IsEnabled { get; }

    ValueTask SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public enum TxtLookupStatus
{
    Found = 0,
    NotFound = 1,
    Failed = 2,
}

public sealed record TxtLookup(TxtLookupStatus Status, IReadOnlyCollection<string> Values);

/// <summary>Consulta DNS de registros TXT (verificação de domínio próprio).</summary>
public interface IDnsTxtResolver
{
    Task<TxtLookup> ResolveAsync(string name, CancellationToken cancellationToken);
}
