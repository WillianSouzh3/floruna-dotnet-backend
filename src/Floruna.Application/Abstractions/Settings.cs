namespace Floruna.Application.Abstractions;

/// <summary>Domínio da plataforma e loja padrão (Options Pattern; a Infrastructure faz o bind).</summary>
public sealed class PlatformSettings
{
    public const string SectionName = "Platform";

    /// <summary>Domínio raiz da plataforma (lojas em <c>{slug}.{dominio}</c>).</summary>
    public string Domain { get; set; } = "floruna.com.br";

    /// <summary>Loja servida no domínio raiz, em <c>localhost</c> e por IP.</summary>
    public string DefaultTenantSlug { get; set; } = "flora-lis";

    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed class AuthenticationSettings
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// 2FA por e-mail no login da equipe. Só vale se houver SMTP configurado:
    /// sem e-mail, o código nunca chegaria e o dono ficaria trancado para fora
    /// (mesma regra do Nest).
    /// </summary>
    public bool TwoFactorEnabled { get; set; } = true;
}
