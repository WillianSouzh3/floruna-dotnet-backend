using Floruna.Api.Security;
using Floruna.Application.Messaging;
using Floruna.Application.Tenants;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Floruna.Api.Controllers;

public sealed record SignupTenantRequest(string? StoreName, string? Slug, string? AdminName, string? Email, string? Password);

public sealed record RequestDomainRequest(string? Domain);

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

    /// <summary>Dados, plano, limites e consumo da loja atual.</summary>
    [HttpGet("me")]
    [Authorize(Roles = Roles.Admins + "," + Roles.Cashier)]
    [ProducesResponseType<CurrentTenantResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetCurrentTenantQuery(), cancellationToken), Ok);

    /// <summary>Estado e instruções de DNS do domínio próprio (<c>{ domain: null }</c> se não houver).</summary>
    [HttpGet("me/domain")]
    [Authorize(Roles = Roles.Master + "," + Roles.SuperAdmin)]
    public async Task<IActionResult> DomainStatus(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetDomainStatusQuery(), cancellationToken);
        return FromResult(result, status => status.Instructions is null ? Ok(new { domain = (string?)null }) : Ok(status.Instructions));
    }

    /// <summary>Reivindica um domínio próprio e devolve o TXT/CNAME a configurar.</summary>
    [HttpPost("me/domain")]
    [Authorize(Roles = Roles.Master + "," + Roles.SuperAdmin)]
    [RequiresPlanFeature(PlanFeature.CustomDomain)]
    [ProducesResponseType<DomainInstructions>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestDomain(RequestDomainRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new RequestCustomDomainCommand(body.Domain ?? string.Empty, CurrentActor()), cancellationToken), Ok);
    }

    /// <summary>Consulta o DNS e ativa o domínio se o TXT conferir.</summary>
    [HttpPost("me/domain/verify")]
    [Authorize(Roles = Roles.Master + "," + Roles.SuperAdmin)]
    [RequiresPlanFeature(PlanFeature.CustomDomain)]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    [ProducesResponseType<DomainInstructions>(StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyDomain(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new VerifyCustomDomainCommand(CurrentActor()), cancellationToken), Ok);

    [HttpDelete("me/domain")]
    [Authorize(Roles = Roles.Master + "," + Roles.SuperAdmin)]
    public async Task<IActionResult> RemoveDomain(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new RemoveCustomDomainCommand(CurrentActor()), cancellationToken));
}
