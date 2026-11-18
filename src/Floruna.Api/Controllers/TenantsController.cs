using Floruna.Api.Security;
using Floruna.Application.Messaging;
using Floruna.Application.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Floruna.Api.Controllers;

public sealed record SignupTenantRequest(string? StoreName, string? Slug, string? AdminName, string? Email, string? Password);

[Route(Contracts.ApiVersion.RoutePrefix + "/tenants")]
public sealed class TenantsController(ISender sender) : ApiController
{
    /// <summary>Cadastro de nova loja: cria a loja, o usuário master e inicia o teste de 14 dias do Pro.</summary>
    [HttpPost("signup")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    [ProducesResponseType<SignupResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Signup(SignupTenantRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(
            new SignupTenantCommand(
                body.StoreName ?? string.Empty,
                body.Slug ?? string.Empty,
                body.AdminName ?? string.Empty,
                body.Email ?? string.Empty,
                body.Password ?? string.Empty),
            cancellationToken);
        return FromResult(result, response => StatusCode(StatusCodes.Status201Created, response));
    }
}
