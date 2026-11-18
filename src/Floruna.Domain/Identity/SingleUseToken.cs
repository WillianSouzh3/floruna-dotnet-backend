using Floruna.SharedKernel;

namespace Floruna.Domain.Identity;

/// <summary>Para que serve a credencial de uso único.</summary>
public enum SingleUseTokenPurpose
{
    /// <summary>Código de 6 dígitos do 2FA por e-mail (10 min).</summary>
    TwoFactorCode = 0,

    /// <summary>Link de "esqueci minha senha" (30 min).</summary>
    PasswordReset = 1,

    /// <summary>Entrada do suporte numa loja (60 s, troca por sessão de 15 min).</summary>
    Impersonation = 2,
}

/// <summary>
/// Credencial de UM uso só, com validade curta. Um único modelo para três
/// fluxos (2FA, redefinição de senha, impersonação), porque as regras são as mesmas:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Guarda só o HASH do segredo. Quem lê o banco não consegue usar o link/código.</item>
/// <item>Vale uma vez: o consumo é um compare-and-swap no banco
/// (<c>UPDATE ... WHERE consumed_at IS NULL</c>). Duas requisições simultâneas
/// com o mesmo link: só uma vence.</item>
/// <item>Pertence a uma loja: um link da loja A não funciona no host da loja B.</item>
/// </list>
/// </remarks>
public sealed class SingleUseToken : Entity<Guid>, ITenantOwned
{
    private SingleUseToken()
        : base(Guid.Empty)
    {
        SecretHash = string.Empty;
    }

    private SingleUseToken(TenantId tenantId, SingleUseTokenPurpose purpose, StaffUserId userId, string secretHash, DateTimeOffset expiresAt, Guid? issuedBy)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        Purpose = purpose;
        UserId = userId;
        SecretHash = secretHash;
        ExpiresAt = expiresAt;
        IssuedBy = issuedBy;
    }

    public TenantId TenantId { get; private set; }

    public SingleUseTokenPurpose Purpose { get; private set; }

    /// <summary>Usuário da equipe a quem a credencial dá acesso.</summary>
    public StaffUserId UserId { get; private set; }

    public string SecretHash { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>Na impersonação: o superadmin que emitiu.</summary>
    public Guid? IssuedBy { get; private set; }

    public static TimeSpan LifetimeOf(SingleUseTokenPurpose purpose) => purpose switch
    {
        SingleUseTokenPurpose.TwoFactorCode => TimeSpan.FromMinutes(10),
        SingleUseTokenPurpose.PasswordReset => TimeSpan.FromMinutes(30),
        SingleUseTokenPurpose.Impersonation => TimeSpan.FromSeconds(60),
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null),
    };

    public static SingleUseToken Issue(TenantId tenantId, SingleUseTokenPurpose purpose, StaffUserId userId, string secretHash, DateTimeOffset now, Guid? issuedBy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretHash);
        return new SingleUseToken(tenantId, purpose, userId, secretHash, now.Add(LifetimeOf(purpose)), issuedBy);
    }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;
}

/// <summary>Persistência das credenciais. O consumo é atômico (ver <see cref="TryConsumeAsync"/>).</summary>
public interface ISingleUseTokenRepository
{
    void Add(SingleUseToken token);

    /// <summary>Apaga as credenciais ainda não usadas do usuário para o mesmo propósito (pedir de novo invalida o anterior).</summary>
    Task RevokePendingAsync(StaffUserId userId, SingleUseTokenPurpose purpose, CancellationToken cancellationToken);

    /// <summary>
    /// Encontra a credencial válida com este hash e a marca como usada NUMA operação
    /// condicional. Devolve null se não existe, venceu ou outra requisição já usou.
    /// </summary>
    Task<SingleUseToken?> TryConsumeAsync(SingleUseTokenPurpose purpose, string secretHash, DateTimeOffset now, CancellationToken cancellationToken);
}
