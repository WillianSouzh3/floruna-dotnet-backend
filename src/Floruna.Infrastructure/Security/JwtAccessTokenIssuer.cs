using Floruna.Application.Abstractions;
using Floruna.Domain.Customers;
using Floruna.Domain.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Floruna.Infrastructure.Security;

/// <summary>
/// Emite o JWT com EXATAMENTE os claims do Nest (<c>JwtPayload</c> em
/// <c>common/roles.ts</c>): <c>sub, email, name, role, tenantId,
/// sessionVersion, locale</c>, mais <c>iat</c>/<c>exp</c>.
/// </summary>
/// <remarks>Singleton: só guarda a chave e o handler, ambos thread-safe.</remarks>
internal sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider clock) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>Sessão de suporte: curta de propósito (e auditada na emissão).</summary>
    public static readonly TimeSpan ImpersonationLifetime = TimeSpan.FromMinutes(15);

    private const string TwoFactorPurpose = "2fa";

    public string IssueFor(StaffUser user) => Issue(StaffClaims(user), options.Value.StaffTokenLifetime);

    public string IssueImpersonationFor(StaffUser master, Guid impersonatedBy)
    {
        var claims = StaffClaims(master);
        claims["impersonatedBy"] = impersonatedBy.ToString();
        return Issue(claims, ImpersonationLifetime);
    }

    /// <summary>
    /// Só <c>sub</c> + <c>purpose</c>: sem <c>role</c> nem <c>tenantId</c>, este token
    /// é recusado como sessão (e a autenticação também recusa qualquer token com <c>purpose</c>).
    /// </summary>
    public string IssueTwoFactorPending(StaffUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Issue(new Dictionary<string, object> { ["sub"] = user.Id.ToString(), ["purpose"] = TwoFactorPurpose }, TimeSpan.FromMinutes(10));
    }

    public StaffUserId? ReadTwoFactorPending(string pendingToken)
    {
        var result = _handler.ValidateTokenAsync(pendingToken, new TokenValidationParameters
        {
            IssuerSigningKey = options.Value.CreateSigningKey(),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero,
        }).GetAwaiter().GetResult(); // validação de HMAC é síncrona por natureza

        if (!result.IsValid
            || !result.Claims.TryGetValue("purpose", out var purpose) || purpose as string != TwoFactorPurpose
            || !result.Claims.TryGetValue("sub", out var sub) || !Guid.TryParse(sub as string, out var userId))
        {
            return null;
        }

        return new StaffUserId(userId);
    }

    private static Dictionary<string, object> StaffClaims(StaffUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id.ToString(),
            ["email"] = user.Email.Value,
            ["name"] = user.Name,
            ["role"] = user.Role,
            ["tenantId"] = user.TenantId.ToString(),
            ["sessionVersion"] = user.SessionVersion, // número, como no Nest
        };
        if (user.Locale is not null)
        {
            claims["locale"] = user.Locale;
        }

        return claims;
    }

    public string IssueFor(Customer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return Issue(
            new Dictionary<string, object>
            {
                ["sub"] = customer.Id.ToString(),
                ["email"] = customer.Email.Value,
                ["name"] = customer.Name,
                ["role"] = Roles.Customer,
                ["tenantId"] = customer.TenantId.ToString(),
            },
            options.Value.CustomerTokenLifetime);
    }

    private string Issue(Dictionary<string, object> claims, TimeSpan lifetime)
    {
        var now = clock.GetUtcNow();
        claims["iat"] = now.ToUnixTimeSeconds();
        claims["exp"] = now.Add(lifetime).ToUnixTimeSeconds();

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = new SigningCredentials(options.Value.CreateSigningKey(), SecurityAlgorithms.HmacSha256),
        });
    }
}
