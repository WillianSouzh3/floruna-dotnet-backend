using System.Security.Claims;
using Floruna.Api.Security;
using Floruna.Application.Messaging;
using Floruna.Application.Pos;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Floruna.Api.Controllers;

public sealed record OpenCashSessionRequest(long? OpeningFloat);

public sealed record CashMovementRequest(string? Type, long? Amount, string? Reason);

public sealed record CloseCashSessionRequest(long? CountedCash);

/// <summary>Controle de caixa do PDV: abertura, sangria/suprimento e fechamento com conferência.</summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/cash-sessions")]
[Authorize(Roles = Roles.PointOfSale)]
[RequiresPlanFeature(PlanFeature.Pdv)]
public sealed class CashSessionsController(ISender sender) : ApiController
{
    /// <summary>Caixa aberto do operador, com o esperado AO VIVO; <c>{ session: null }</c> se não houver.</summary>
    [HttpGet("current")]
    public async Task<IActionResult> Current(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetCurrentCashSessionQuery(OperatorId()), cancellationToken),
            session => session is null ? Ok(new { session = (object?)null }) : Ok(session));

    [HttpPost("open")]
    public async Task<IActionResult> Open(OpenCashSessionRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new OpenCashSessionCommand(body.OpeningFloat ?? -1, OperatorName(), CurrentActor()), cancellationToken),
            s => StatusCode(StatusCodes.Status201Created, s));
    }

    [HttpPost("movement")]
    public async Task<IActionResult> Movement(CashMovementRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new AddCashMovementCommand(body.Type, body.Amount ?? 0, body.Reason, OperatorName(), CurrentActor()), cancellationToken), Ok);
    }

    [HttpPost("close")]
    public async Task<IActionResult> Close(CloseCashSessionRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new CloseCashSessionCommand(body.CountedCash ?? -1, CurrentActor()), cancellationToken), Ok);
    }

    /// <summary>Caixas fechados (relatório).</summary>
    [HttpGet]
    public async Task<IActionResult> Closed([FromQuery] PageParameters query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FromResult(await sender.Send(new ListClosedCashSessionsQuery(query.SafePage, query.SafeLimit), cancellationToken), Ok);
    }

    private Guid OperatorId() => Guid.Parse(User.FindFirstValue("sub")!);

    private string OperatorName() => User.FindFirstValue("name") ?? string.Empty;
}
