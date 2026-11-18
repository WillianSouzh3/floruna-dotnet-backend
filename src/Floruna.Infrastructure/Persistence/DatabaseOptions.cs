using System.ComponentModel.DataAnnotations;

namespace Floruna.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    /// <summary>Vem de <c>ConnectionStrings:Floruna</c> (env: <c>ConnectionStrings__Floruna</c>).</summary>
    [Required(ErrorMessage = "Configure ConnectionStrings:Floruna.")]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Aplica migrations na subida. Ligado só em dev/teste: em produção
    /// as migrations rodam como etapa do deploy (bundle do EF), antes de
    /// trocar as tasks, para várias réplicas não migrarem ao mesmo tempo.
    /// </summary>
    public bool MigrateOnStartup { get; set; }
}

/// <summary>Dados iniciais de desenvolvimento (equivale ao <c>pnpm seed</c>, só a loja e o admin).</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public string TenantSlug { get; set; } = "flora-lis";

    public string TenantName { get; set; } = "Flora & Lis";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }

    public string AdminName { get; set; } = "Administrador";

    /// <summary>Superadmin da plataforma (mora na loja de seed e navega por todas).</summary>
    public string? SuperAdminEmail { get; set; }

    public string? SuperAdminPassword { get; set; }
}
