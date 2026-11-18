using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Floruna.Infrastructure.Security;

/// <summary>
/// Configuração do JWT. Compatível com o Nest: HS256 com o MESMO
/// <c>AUTH_SECRET</c>, então o <c>proxy.ts</c> do Next valida os tokens
/// emitidos pelo .NET (e vice-versa) sem mudança.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Segredo HMAC. Aceita <c>Jwt:Secret</c> ou a variável legada <c>AUTH_SECRET</c>.</summary>
    [Required(ErrorMessage = "Configure Jwt:Secret (ou AUTH_SECRET).")]
    [MinLength(32, ErrorMessage = "Jwt:Secret precisa de ao menos 32 caracteres.")]
    public string Secret { get; set; } = string.Empty;

    /// <summary>Sessão do painel (Nest: <c>JWT_ADMIN_EXPIRES_IN=8h</c>).</summary>
    public TimeSpan StaffTokenLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Sessão do cliente (Nest: <c>JWT_CUSTOMER_EXPIRES_IN=7d</c>).</summary>
    public TimeSpan CustomerTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>A chave é o segredo em UTF-8, como o <c>jsonwebtoken</c>/<c>jose</c> do Node fazem.</summary>
    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(Secret));
}
