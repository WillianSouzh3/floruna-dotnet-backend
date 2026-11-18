using Floruna.Domain.Identity;
using Floruna.SharedKernel;

namespace Floruna.Domain.Customers;

public readonly record struct CustomerId(Guid Value)
{
    public static CustomerId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Cliente da vitrine de UMA loja. O mesmo e-mail pode ter conta em lojas
/// diferentes (são contas diferentes): a unicidade é (TenantId, Email).
/// </summary>
public sealed class Customer : AggregateRoot<CustomerId>, ITenantOwned
{
    private Customer()
        : base(default)
    {
        Name = string.Empty;
        Email = null!;
        PasswordHash = string.Empty;
        Phone = string.Empty;
    }

    private Customer(CustomerId id, TenantId tenantId, string name, Email email, string passwordHash, string phone, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        Name = name;
        Email = email;
        PasswordHash = passwordHash;
        Phone = phone;
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public string Name { get; private set; }

    public Email Email { get; private set; }

    public string PasswordHash { get; private set; }

    public string Phone { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<Customer> Register(TenantId tenantId, string? name, Email email, string passwordHash, string? phone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is < 2 or > 80)
        {
            return IdentityErrors.NameInvalid;
        }

        return new Customer(CustomerId.New(), tenantId, trimmedName, email, passwordHash, phone?.Trim() ?? string.Empty, now);
    }
}
