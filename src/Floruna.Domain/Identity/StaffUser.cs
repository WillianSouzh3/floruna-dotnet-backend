using Floruna.SharedKernel;

namespace Floruna.Domain.Identity;

public readonly record struct StaffUserId(Guid Value)
{
    public static StaffUserId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Usuário da equipe da loja (painel/PDV). Corresponde ao <c>AdminUser</c> do Nest.
/// </summary>
/// <remarks>
/// O agregado guarda o HASH da senha, nunca a senha. Quem calcula e
/// confere o hash é uma porta da camada de aplicação
/// (<c>IPasswordHasher</c>): o domínio não sabe o que é BCrypt.
/// </remarks>
public sealed class StaffUser : AggregateRoot<StaffUserId>, ITenantOwned
{
    private StaffUser()
        : base(default)
    {
        Name = string.Empty;
        Email = null!;
        PasswordHash = string.Empty;
        Role = string.Empty;
    }

    private StaffUser(StaffUserId id, TenantId tenantId, string name, Email email, string passwordHash, string role, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        Name = name;
        Email = email;
        PasswordHash = passwordHash;
        Role = role;
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public string Name { get; private set; }

    public Email Email { get; private set; }

    public string PasswordHash { get; private set; }

    public string Role { get; private set; }

    public string? Locale { get; private set; }

    /// <summary>
    /// Versão da sessão. Vai dentro do JWT; se mudar, todos os tokens
    /// emitidos antes deixam de valer (RN-07). É assim que se "desloga"
    /// alguém com JWT, que por natureza não tem estado no servidor.
    /// </summary>
    public int SessionVersion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<StaffUser> Create(TenantId tenantId, string? name, Email email, string passwordHash, string role, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is < 2 or > 80)
        {
            return IdentityErrors.NameInvalid;
        }

        if (!Roles.Staff.Contains(role))
        {
            return IdentityErrors.RoleInvalid;
        }

        return new StaffUser(StaffUserId.New(), tenantId, trimmedName, email, passwordHash, role, now);
    }

    /// <summary>Troca o papel. O token antigo carrega o papel antigo, então as sessões são revogadas.</summary>
    public Result ChangeRole(string role)
    {
        if (!TeamPolicy.AssignableRoles.Contains(role))
        {
            return IdentityErrors.RoleInvalid;
        }

        if (role != Role)
        {
            Role = role;
            RevokeSessions();
        }

        return Result.Success();
    }

    /// <summary>Invalida todos os tokens já emitidos para este usuário.</summary>
    public void RevokeSessions() => SessionVersion++;

    /// <summary>Troca de senha sempre revoga as sessões abertas.</summary>
    public void ChangePasswordHash(string newPasswordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPasswordHash);
        PasswordHash = newPasswordHash;
        RevokeSessions();
    }
}
