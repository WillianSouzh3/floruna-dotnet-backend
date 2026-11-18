using Floruna.Application.Identity;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Floruna.Api.Controllers;

public sealed record CreateTeamMemberRequest(string? Name, string? Email, string? Password, string? Role);

public sealed record UpdateTeamMemberRequest(string? Role, string? Password);

public sealed record ChangeMyPasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>Equipe da loja (<c>/users</c>), mesmo contrato do <c>UsersController</c> do Nest.</summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/users")]
public sealed class UsersController(ISender sender) : ApiController
{
    private const string Owner = Roles.Master + "," + Roles.SuperAdmin;

    [HttpGet]
    [Authorize(Roles = Roles.Admins)]
    [ProducesResponseType<TeamResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ListTeamQuery(), cancellationToken), Ok);

    [HttpPost]
    [Authorize(Roles = Owner)]
    [ProducesResponseType<TeamMemberResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CreateTeamMemberRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(
            new CreateTeamMemberCommand(body.Name ?? string.Empty, body.Email ?? string.Empty, body.Password ?? string.Empty, body.Role ?? string.Empty, CurrentActor()),
            cancellationToken);
        return FromResult(result, member => StatusCode(StatusCodes.Status201Created, member));
    }

    /// <summary>Troca a própria senha (exige a atual). Revoga as sessões: é preciso entrar de novo.</summary>
    [HttpPatch("me/password")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> ChangeMyPassword(ChangeMyPasswordRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(
            new ChangeMyPasswordCommand(CurrentUserId, body.CurrentPassword ?? string.Empty, body.NewPassword ?? string.Empty), cancellationToken);
        return result.IsSuccess ? Ok(new { ok = true }) : Failure(result.Error);
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Roles = Owner)]
    [ProducesResponseType<TeamMemberResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, UpdateTeamMemberRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new UpdateTeamMemberCommand(id, body.Role, body.Password, CurrentActor()), cancellationToken);
        return FromResult(result, Ok);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Owner)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new RemoveTeamMemberCommand(id, CurrentActor()), cancellationToken));
}
