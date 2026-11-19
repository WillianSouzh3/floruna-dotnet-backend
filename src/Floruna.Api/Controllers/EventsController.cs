using System.Security.Claims;
using Floruna.Api.Security;
using Floruna.Application.Messaging;
using Floruna.Application.Workshops;
using Floruna.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Floruna.Api.Controllers;

public sealed record EventRequest(
    string? Title, string? Slug, string? ShortDescription, string? Description, string? Date, string? Location, string? Image,
    long? Price, int? Capacity, bool? IsActive)
{
    public EventInput ToInput() => new(Title, Slug, ShortDescription, Description, Date, Location, Image, Price, Capacity, IsActive);
}

public sealed record EventQueryParameters(int? Page, int? Limit, string? OnlyActive);

public sealed record RegisterRequest(string? Name, string? Phone, string? Email);

/// <summary>Oficinas e eventos da loja (contrato do Nest: <c>/events</c>).</summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/events")]
public sealed class EventsController(ISender sender) : ApiController
{
    /// <summary>Público. A equipe autenticada vê também as desativadas.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> List([FromQuery] EventQueryParameters query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var command = new ListEventsQuery(Math.Max(1, query.Page ?? 1), Math.Clamp(query.Limit ?? 20, 1, 100), query.OnlyActive == "true", IsStaff());
        return FromResult(await sender.Send(command, cancellationToken), Ok);
    }

    [HttpGet("{slug}")]
    [AllowAnonymous]
    public async Task<IActionResult> BySlug(string slug, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetEventBySlugQuery(slug, IsStaff()), cancellationToken), Ok);

    [HttpPost]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Create(EventRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new CreateEventCommand(body.ToInput()), cancellationToken), e => StatusCode(StatusCodes.Status201Created, e));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Update(Guid id, EventRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new UpdateEventCommand(id, body.ToInput()), cancellationToken), Ok);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new DeleteEventCommand(id), cancellationToken), _ => Ok(new { success = true }));

    /// <summary>Inscrição pública: ocupa a vaga e grava a inscrição na mesma transação.</summary>
    [HttpPost("{id:guid}/registrations")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.CheckoutPolicy)]
    public async Task<IActionResult> Register(Guid id, RegisterRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new RegisterForEventCommand(id, body.Name, body.Phone, body.Email), cancellationToken),
            e => StatusCode(StatusCodes.Status201Created, e));
    }

    [HttpGet("{id:guid}/registrations")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Registrations(Guid id, [FromQuery] PageParameters query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FromResult(await sender.Send(new ListRegistrationsQuery(id, query.SafePage, query.SafeLimit), cancellationToken), Ok);
    }

    private bool IsStaff() => User.FindFirstValue("role") is { } role && Roles.Staff.Contains(role);
}
