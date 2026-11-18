using System.Threading.RateLimiting;
using Floruna.Api.Contracts;
using Floruna.SharedKernel;
using Microsoft.Extensions.Options;

namespace Floruna.Api.Security;

public sealed class RateLimitingSettings
{
    public const string SectionName = "RateLimiting";

    /// <summary>Tentativas por janela em login/cadastro (Nest: 5 por minuto).</summary>
    public int AuthPermitLimit { get; set; } = 5;

    public TimeSpan AuthWindow { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Rate limiting NATIVO do ASP.NET Core (padrão de nuvem "Rate Limiting").
/// Partição por loja + IP: um ataque a uma loja não bloqueia as outras.
/// </summary>
/// <remarks>
/// Em memória, por réplica. Com várias tasks no ECS, o limite efetivo
/// multiplica pelo número de réplicas; a defesa de borda fica no AWS WAF
/// (regra rate-based), e o limite distribuído pode ir para o Redis.
/// </remarks>
internal static class RateLimitingSetup
{
    public const string AuthPolicy = "auth";

    public static IServiceCollection AddFlorunaRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitingSettings>(configuration.GetSection(RateLimitingSettings.SectionName));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var error = new Error("RATE_LIMITED", "Muitas tentativas. Aguarde um minuto e tente novamente.");
                await context.HttpContext.Response.WriteAsJsonAsync(ApiResponses.Error(context.HttpContext, error), cancellationToken);
            };

            options.AddPolicy(AuthPolicy, http =>
            {
                var settings = http.RequestServices.GetRequiredService<IOptions<RateLimitingSettings>>().Value;
                var tenantHost = http.Request.Headers["x-tenant-host"].ToString();
                var key = $"{tenantHost}|{http.Connection.RemoteIpAddress}";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.AuthPermitLimit,
                    Window = settings.AuthWindow,
                    QueueLimit = 0,
                });
            });
        });

        return services;
    }
}
