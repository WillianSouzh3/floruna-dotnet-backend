using Floruna.Application.Abstractions;
using Floruna.Application.Identity;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Floruna.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Floruna.Api.Security;

/// <summary>Falha de sessão com mensagem para o usuário (vai para o envelope de erro 401).</summary>
internal sealed class SessionRejectedException(string message) : Exception(message);

/// <summary>
/// Guarda o MOTIVO da falha de autenticação na requisição. Necessário porque o
/// AuthenticationMiddleware só publica o resultado quando a autenticação DÁ CERTO;
/// na falha, quem escreve o 401 não teria como saber o porquê.
/// </summary>
internal static class AuthFailure
{
    private const string Key = "floruna.auth.failure";

    public static void Record(HttpContext context, string message) => context.Items[Key] = message;

    public static string? Read(HttpContext context) => context.Items.TryGetValue(Key, out var value) ? value as string : null;
}

internal static class AuthenticationSetup
{
    public static IServiceCollection AddFlorunaAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // A configuração lê JwtOptions do DI (já validadas), por isso fica numa classe à parte.
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();
        return services;
    }

    private sealed class ConfigureJwtBearer(IOptions<JwtOptions> jwtOptions) : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

        public void Configure(string? name, JwtBearerOptions options)
        {
            // Mantém os nomes curtos dos claims (sub, role) em vez de mapear para URIs do WS-*.
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                IssuerSigningKey = jwtOptions.Value.CreateSigningKey(),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256], // só HS256 (impede o ataque "alg: none")
                ValidateIssuer = false,   // o Nest não emite iss/aud
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "name",
                RoleClaimType = "role",
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = ValidateSessionAsync,
                OnAuthenticationFailed = context =>
                {
                    if (context.Exception is SecurityTokenExpiredException)
                    {
                        AuthFailure.Record(context.HttpContext, "Sessão expirada. Entre novamente.");
                    }

                    return Task.CompletedTask;
                },
            };
        }

        /// <summary>
        /// Depois da assinatura, as regras de NEGÓCIO da sessão (JwtStrategy do Nest):
        /// o token é desta loja? A sessão da equipe não foi revogada?
        /// </summary>
        private static async Task ValidateSessionAsync(TokenValidatedContext context)
        {
            static void Reject(TokenValidatedContext context, string message)
            {
                AuthFailure.Record(context.HttpContext, message);
                context.Fail(new SessionRejectedException(message));
            }

            var principal = context.Principal!;
            var role = principal.FindFirst("role")?.Value ?? string.Empty;
            var tokenTenant = principal.FindFirst("tenantId")?.Value;
            var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>().Tenant;

            // Tokens de propósito específico (ex.: etapa do 2FA) nunca valem como sessão.
            if (principal.HasClaim(c => c.Type == "purpose"))
            {
                Reject(context, "Token inválido.");
                return;
            }

            // O superadmin navega por todas as lojas: não exige que o token seja da loja do host.
            if (role != Roles.SuperAdmin)
            {
                if (tenant is null)
                {
                    Reject(context, "Não foi possível resolver a loja desta sessão.");
                    return;
                }

                if (!string.Equals(tokenTenant, tenant.Id.ToString(), StringComparison.Ordinal))
                {
                    Reject(context, IdentityErrors.SessionFromOtherTenant.Message);
                    return;
                }
            }

            if (Roles.Staff.Contains(role))
            {
                var sessionVersion = int.TryParse(principal.FindFirst("sessionVersion")?.Value, out var v) ? v : 0;
                if (!Guid.TryParse(principal.FindFirst("sub")?.Value, out var userId) || !Guid.TryParse(tokenTenant, out var tenantId))
                {
                    Reject(context, IdentityErrors.SessionRevoked.Message);
                    return;
                }

                var sender = context.HttpContext.RequestServices.GetRequiredService<ISender>();
                var check = await sender.Send(
                    new ValidateStaffSessionQuery(userId, tenantId, role, sessionVersion), context.HttpContext.RequestAborted);
                if (check.IsFailure)
                {
                    Reject(context, check.Error.Message);
                }
            }
        }
    }
}
