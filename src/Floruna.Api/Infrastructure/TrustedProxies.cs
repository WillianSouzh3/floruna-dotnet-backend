using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Floruna.Api.Infrastructure;

/// <summary>
/// Atrás de um load balancer (ALB na AWS), o IP que chega na API é o do ALB.
/// O IP real vem no header <c>X-Forwarded-For</c>, mas esse header só é
/// confiável se vier de um proxy CONHECIDO: senão qualquer cliente forjaria o IP
/// e burlaria o rate limit e o bloqueio de login.
/// </summary>
/// <remarks>
/// Configure <c>Network:TrustedProxies</c> com as faixas da VPC (ex.: <c>10.0.0.0/16</c>).
/// Sem configuração, só o loopback é confiável (padrão do ASP.NET).
/// </remarks>
internal static class TrustedProxies
{
    public static IServiceCollection AddTrustedProxies(this IServiceCollection services, IConfiguration configuration)
    {
        var networks = configuration.GetSection("Network:TrustedProxies").Get<string[]>() ?? [];
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            foreach (var cidr in networks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            }
        });
        return services;
    }
}
