using System.Text.RegularExpressions;
using Floruna.Api.Contracts;
using Floruna.Application.Abstractions;
using Floruna.Domain.Platform;

namespace Floruna.Api.Infrastructure;

/// <summary>
/// Descobre a loja da requisição pelo host e a coloca no
/// <see cref="TenantContext"/> (equivale ao <c>TenantResolverMiddleware</c> do Nest).
/// </summary>
/// <remarks>
/// <para>O BFF Next.js manda o host visitado em <c>x-tenant-host</c>. Confiar
/// nesse header só é seguro porque a API fica na rede privada (na AWS:
/// subnet privada, sem rota pública). Mesmo se alguém o forjasse, o JWT de
/// outra loja seria recusado na autenticação.</para>
/// <para>Host desconhecido: segue SEM loja. Qualquer acesso a dado de loja
/// então falha (fail-closed) em vez de cair numa loja "padrão".</para>
/// </remarks>
internal sealed partial class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string TenantHostHeader = "x-tenant-host";

    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver, TenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path.Value ?? string.Empty;

        // O health não pode depender do banco nem do status da loja.
        if (HealthPath().IsMatch(path))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers[TenantHostHeader].ToString();
        var host = header.Length > 0 ? header : context.Request.Host.Value ?? string.Empty;
        var tenant = await resolver.ResolveByHostAsync(host, context.RequestAborted);

        if (tenant is not null)
        {
            // Loja suspensa: só o login do painel continua (o dono precisa entrar para regularizar).
            if (tenant.Status == TenantStatus.Suspended && !ControlPlanePath().IsMatch(path))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(ApiResponses.Error(context, TenantErrors.Suspended));
                return;
            }

            tenantContext.Set(tenant);
        }

        await next(context);
    }

    [GeneratedRegex(@"/health(?:/(?:live|ready))?/?$")]
    private static partial Regex HealthPath();

    [GeneratedRegex(@"/auth/admin/login/?$")]
    private static partial Regex ControlPlanePath();
}
