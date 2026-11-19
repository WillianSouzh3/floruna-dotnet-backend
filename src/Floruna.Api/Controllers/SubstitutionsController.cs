using Floruna.Api.Security;
using Floruna.Application.Messaging;
using Floruna.Application.Substitutions;
using Floruna.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Floruna.Api.Controllers;

public sealed record ConsentBody(string? Token, bool? Approve);

public sealed record OperatorDecisionBody(bool? Approve, string? Reason);

public sealed record SubstitutionPolicyBody(string? DefaultMode, int? PreapprovedCostTolerancePercent, int? ConsentWindowHours);

[Route(Contracts.ApiVersion.RoutePrefix + "/substitutions")]
public sealed class SubstitutionsController(ISender sender) : ApiController
{
    /// <summary>O comprador aprova ou recusa pelo link (público; autorizado pelo token; com limite de tentativas).</summary>
    [HttpPost("{id:guid}/consent")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.CheckoutPolicy)]
    public async Task<IActionResult> Consent(Guid id, ConsentBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new CustomerConsentCommand(id, body.Token, body.Approve ?? false), cancellationToken), Ok);
    }

    /// <summary>O comprador VÊ a proposta pelo link (rota nova).</summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.CheckoutPolicy)]
    public async Task<IActionResult> ByToken(Guid id, [FromQuery] string? token, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetProposalByTokenQuery(id, token), cancellationToken), Ok);

    /// <summary>A loja registra a resposta (ex.: por telefone). Em "perguntar antes", só recusa.</summary>
    [HttpPost("{id:guid}/decision")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Decide(Guid id, OperatorDecisionBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new OperatorDecisionCommand(id, body.Approve ?? false, body.Reason, CurrentActor()), cancellationToken), Ok);
    }

    /// <summary>Aplica uma troca aprovada (se a aplicação automática não pôde, por falta de estoque).</summary>
    [HttpPost("{id:guid}/apply")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Apply(Guid id, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ApplySubstitutionCommand(id, CurrentActor()), cancellationToken), Ok);
}

[Route(Contracts.ApiVersion.RoutePrefix + "/substitution-policy")]
[Authorize(Roles = Roles.Admins)]
public sealed class SubstitutionPolicyController(ISender sender) : ApiController
{
    [HttpGet]
    public async Task<IActionResult> Current(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetSubstitutionPolicyQuery(), cancellationToken), Ok);

    /// <summary>Publica uma nova versão. As propostas já criadas guardam a versão sob a qual foram decididas.</summary>
    [HttpPut]
    public async Task<IActionResult> Publish(SubstitutionPolicyBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new PublishSubstitutionPolicyCommand(body.DefaultMode, body.PreapprovedCostTolerancePercent, body.ConsentWindowHours), cancellationToken), Ok);
    }
}
