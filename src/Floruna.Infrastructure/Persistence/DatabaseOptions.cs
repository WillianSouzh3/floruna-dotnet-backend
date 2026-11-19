using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Npgsql;

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

/// <summary>
/// Monta a connection string. Em dev, vem pronta (<c>ConnectionStrings:Floruna</c>). Na AWS, o
/// ECS injeta os CAMPOS do segredo do RDS (host, porta, banco, usuário, senha) como variáveis
/// <c>Database__*</c>: a senha nunca aparece numa string montada fora da aplicação.
/// </summary>
public static class DatabaseConnection
{
    public static string Compose(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.GetConnectionString("Floruna") is { Length: > 0 } ready)
        {
            return ready;
        }

        var section = configuration.GetSection("Database");
        if (string.IsNullOrWhiteSpace(section["Host"]))
        {
            return string.Empty; // a validação das opções acusa na subida
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = section["Host"],
            Port = section.GetValue("Port", 5432),
            Database = section["Name"] ?? "floruna",
            Username = section["Username"],
            Password = section["Password"],
            // RDS aceita TLS; Require cifra o tráfego. VerifyFull exigiria o bundle de CAs do RDS na imagem (backlog).
            SslMode = Enum.Parse<SslMode>(section["SslMode"] ?? nameof(SslMode.Require)),
            MaxPoolSize = section.GetValue("MaxPoolSize", 50),
            // Sem Kerberos/GSS: evita a tentativa (e o aviso "libgssapi_krb5") a cada conexão nova.
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString;
    }
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
