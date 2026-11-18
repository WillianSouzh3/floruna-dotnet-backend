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

    public string IssueFor(StaffUser user)
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

        return Issue(claims, options.Value.StaffTokenLifetime);
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
