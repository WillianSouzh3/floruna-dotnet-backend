using Floruna.Domain.Customers;
using Floruna.Domain.Identity;

namespace Floruna.Application.Abstractions;

/// <summary>
/// Unit of Work: confirma TODAS as mudanças rastreadas numa transação só.
/// Os handlers chamam explicitamente, para deixar visível ONDE o estado é gravado.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Violação de índice único, traduzida do erro do banco (23505 no PostgreSQL).</summary>
public sealed class UniqueConstraintViolationException(string constraintName, Exception innerException)
    : Exception($"Violação de unicidade: {constraintName}", innerException)
{
    public string ConstraintName { get; } = constraintName;
}

/// <summary>Hash de senha. O domínio só conhece "hash"; o algoritmo (BCrypt) é detalhe.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>
    /// Confere a senha. Com <paramref name="hash"/> nulo (conta inexistente),
    /// compara contra um hash fictício para gastar o MESMO tempo: assim o
    /// tempo de resposta não revela se o e-mail tem conta.
    /// </summary>
    bool Verify(string password, string? hash);
}

/// <summary>Emite o JWT da sessão, no formato que o front Next.js já entende.</summary>
public interface IAccessTokenIssuer
{
    string IssueFor(StaffUser user);

    string IssueFor(Customer customer);
}

/// <summary>Resolve a loja a partir do host visitado (subdomínio ou domínio próprio).</summary>
public interface ITenantResolver
{
    Task<ResolvedTenant?> ResolveByHostAsync(string host, CancellationToken cancellationToken);

    /// <summary>Descarta o cache (após criar/alterar loja).</summary>
    void Invalidate();
}
