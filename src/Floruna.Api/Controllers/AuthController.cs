using System.Security.Claims;
using Floruna.Api.Security;
using Floruna.Application.Identity;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Floruna.Api.Controllers;

// Corpos de requisição com campos ANULÁVEIS: a validação (com mensagens em
// pt-BR, iguais às do Nest) fica no FluentValidation do caso de uso, não no binding.
public sealed record LoginRequest(string? Email, string? Password);

public sealed record RegisterCustomerRequest(string? Name, string? Email, string? Password, string? Phone);

/// <summary>Rotas de <c>/auth</c>, mesmo contrato do <c>AuthController</c> do Nest.</summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/auth")]
public sealed class AuthController(ISender sender) : ApiController
{
    /// <summary>Login do painel (equipe da loja).</summary>
    [HttpPost("admin/login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    [ProducesResponseType<AuthResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> LoginStaff(LoginRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new LoginStaffCommand(body.Email ?? string.Empty, body.Password ?? string.Empty), cancellationToken);
        return FromResult(result, Ok);
    }

    /// <summary>Cadastro de cliente na vitrine. Já devolve autenticado.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    [ProducesResponseType<AuthResult>(StatusCodes.Status201Created)]
    public async Task<IActionResult> RegisterCustomer(RegisterCustomerRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(
            new RegisterCustomerCommand(body.Name ?? string.Empty, body.Email ?? string.Empty, body.Password ?? string.Empty, body.Phone),
            cancellationToken);
        return FromResult(result, auth => StatusCode(StatusCodes.Status201Created, auth));
    }

    /// <summary>Login de cliente na vitrine.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    [ProducesResponseType<AuthResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> LoginCustomer(LoginRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new LoginCustomerCommand(body.Email ?? string.Empty, body.Password ?? string.Empty), cancellationToken);
        return FromResult(result, Ok);
    }

    /// <summary>"Sair de todos os dispositivos": invalida todos os tokens já emitidos.</summary>
    [HttpPost("admin/revoke-sessions")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> RevokeSessions(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RevokeStaffSessionsCommand(Guid.Parse(User.FindFirstValue("sub")!)), cancellationToken);
        return result.IsSuccess ? Ok(new { ok = true }) : Failure(result.Error);
    }

    /// <summary>O usuário do token atual (mesmo formato do Nest: <c>{ user: {...} }</c>).</summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var user = new Dictionary<string, object?>
        {
            ["sub"] = User.FindFirstValue("sub"),
            ["email"] = User.FindFirstValue("email"),
            ["name"] = User.FindFirstValue("name"),
            ["role"] = User.FindFirstValue("role"),
            ["tenantId"] = User.FindFirstValue("tenantId"),
        };
        if (int.TryParse(User.FindFirstValue("sessionVersion"), out var sessionVersion))
        {
            user["sessionVersion"] = sessionVersion;
        }

        return Ok(new { user });
    }
}
