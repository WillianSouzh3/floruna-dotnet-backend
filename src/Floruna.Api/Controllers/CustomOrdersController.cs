using Floruna.Api.Security;
using Floruna.Application.CustomOrders;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Floruna.Api.Controllers;

public sealed record CustomOrderRequestBody(
    string? CustomerName, string? CustomerPhone, string? CustomerEmail, string? Occasion, string? ContainerType, string? Size,
    IReadOnlyList<string>? ColorPalette, IReadOnlyList<string>? FlowerPreferences, string? Ribbon, string? CardMessage, string? BudgetRange,
    DateTimeOffset? DesiredDate, string? Notes);

public sealed record CustomOrderQuery(int? Page, int? Limit, string? Status);

public sealed record AnnotateCustomOrderBody(string? Status, long? QuotedPrice, string? AdminNotes);

public sealed record QuoteBody(IReadOnlyList<QuoteLineDto>? Lines, string? Notes)
{
    public QuoteContent ToContent() => new(Lines, Notes);
}

public sealed record PublishQuoteBody(int? ValidityDays);

public sealed record ConvertQuoteBody(Guid? OrderId);

public sealed record QuoteDecisionBody(string? Token, bool? Accept, string? Reason);

/// <summary>Pedido personalizado (configurador da vitrine) e seus orçamentos versionados.</summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/custom-orders")]
public sealed class CustomOrdersController(ISender sender) : ApiController
{
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.CheckoutPolicy)]
    public async Task<IActionResult> Submit(CustomOrderRequestBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var command = new SubmitCustomOrderCommand(body.CustomerName, body.CustomerPhone, body.CustomerEmail, body.Occasion, body.ContainerType, body.Size,
            body.ColorPalette, body.FlowerPreferences, body.Ribbon, body.CardMessage, body.BudgetRange, body.DesiredDate, body.Notes);
        return FromResult(await sender.Send(command, cancellationToken), r => StatusCode(StatusCodes.Status201Created, r));
    }

    [HttpGet]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> List([FromQuery] CustomOrderQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FromResult(await sender.Send(new ListCustomOrdersQuery(Math.Max(1, query.Page ?? 1), Math.Clamp(query.Limit ?? 20, 1, 100), query.Status), cancellationToken), Ok);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetCustomOrderQuery(id), cancellationToken), Ok);

    [HttpPatch("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Annotate(Guid id, AnnotateCustomOrderBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new AnnotateCustomOrderCommand(id, body.Status, body.QuotedPrice, body.AdminNotes), cancellationToken), Ok);
    }

    [HttpGet("{requestId:guid}/quotes")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Quotes(Guid requestId, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ListQuotesQuery(requestId), cancellationToken), Ok);

    /// <summary>Nova versão (rascunho). Revisar uma proposta enviada = nova versão; a anterior continua legível.</summary>
    [HttpPost("{requestId:guid}/quotes")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Draft(Guid requestId, QuoteBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new DraftQuoteCommand(requestId, body.ToContent()), cancellationToken), q => StatusCode(StatusCodes.Status201Created, q));
    }
}

[Route(Contracts.ApiVersion.RoutePrefix + "/custom-quotes")]
public sealed class CustomQuotesController(ISender sender) : ApiController
{
    [HttpPatch("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> UpdateDraft(Guid id, QuoteBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new UpdateQuoteDraftCommand(id, body.ToContent()), cancellationToken), Ok);
    }

    /// <summary>Envia ao cliente. O token do link volta UMA vez, aqui.</summary>
    [HttpPost("{id:guid}/publish")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Publish(Guid id, PublishQuoteBody? body, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new PublishQuoteCommand(id, body?.ValidityDays), cancellationToken), Ok);

    [HttpPost("{id:guid}/convert")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Convert(Guid id, ConvertQuoteBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new ConvertQuoteCommand(id, body.OrderId ?? Guid.Empty), cancellationToken), Ok);
    }

    /// <summary>Resposta do cliente pelo link (público; autorizado pelo token).</summary>
    [HttpPost("{id:guid}/decision")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.CheckoutPolicy)]
    public async Task<IActionResult> Decide(Guid id, QuoteDecisionBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new DecideQuoteCommand(id, body.Token, body.Accept ?? false, body.Reason), cancellationToken), Ok);
    }

    /// <summary>O cliente VÊ a proposta pelo link (rota nova: no Nest só havia a decisão).</summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.CheckoutPolicy)]
    public async Task<IActionResult> ByToken(Guid id, [FromQuery] string? token, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetQuoteByTokenQuery(id, token), cancellationToken), Ok);
}
