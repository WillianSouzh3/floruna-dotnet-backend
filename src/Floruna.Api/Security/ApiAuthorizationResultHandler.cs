using Floruna.Api.Contracts;
using Floruna.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Floruna.Api.Security;

/// <summary>
/// Escreve 401/403 no envelope de erro do contrato, com a mensagem certa
/// (sessão revogada, token de outra loja, plano sem o recurso...).
/// </summary>
internal sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Challenged)
        {
            var message = AuthFailure.Read(context) ?? "Autenticação necessária.";
            await Write(context, Error.Unauthorized("AUTHENTICATION_REQUIRED", message));
            return;
        }

        if (authorizeResult.Forbidden)
        {
            var planBlocked = authorizeResult.AuthorizationFailure?.FailureReasons
                .Any(r => r.Handler is PlanFeatureHandler) ?? false;
            var error = planBlocked
                ? Error.Forbidden("PLAN_UPGRADE_REQUIRED", PlanFeatureHandler.UpgradeRequiredMessage)
                : Error.Forbidden("FORBIDDEN", "Acesso negado.");
            await Write(context, error);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private static Task Write(HttpContext context, Error error)
    {
        context.Response.StatusCode = ApiResponses.StatusCodeFor(error.Type);
        return context.Response.WriteAsJsonAsync(ApiResponses.Error(context, error));
    }
}
