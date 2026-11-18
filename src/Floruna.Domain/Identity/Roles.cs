namespace Floruna.Domain.Identity;

/// <summary>
/// Papéis do sistema. Os valores de string são CONTRATO: viajam no JWT e o
/// front Next.js (<c>proxy.ts</c>) os lê. Não traduzir nem renomear.
/// </summary>
public static class Roles
{
    /// <summary>Administra a plataforma (todas as lojas).</summary>
    public const string SuperAdmin = "superadmin";

    /// <summary>Dono da loja.</summary>
    public const string Master = "master";

    /// <summary>Gerente da loja.</summary>
    public const string Manager = "gerente";

    /// <summary>Operador de balcão: só PDV e caixa (RN-06).</summary>
    public const string Cashier = "caixa";

    /// <summary>Cliente da vitrine (login opcional).</summary>
    public const string Customer = "customer";

    /// <summary>Quem usa o painel administrativo.</summary>
    public const string Admins = SuperAdmin + "," + Master + "," + Manager;

    /// <summary>Quem opera PDV e caixa.</summary>
    public const string PointOfSale = Admins + "," + Cashier;

    public static readonly IReadOnlySet<string> Staff = new HashSet<string>(StringComparer.Ordinal)
    {
        SuperAdmin, Master, Manager, Cashier,
    };
}
