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

public sealed record VerifyOtpRequest(string? PendingToken, string? Code);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Token, string? Password);

public sealed record ImpersonationExchangeRequest(string? Code);

/// <summary>Rotas de <c>/auth</c>, mesmo contrato do <c>AuthController</c> do Nest.</summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/auth")]
public sealed class AuthController(ISender sender) : ApiController
{
    /// <summary>Login do painel. Devolve a sessão OU <c>{ requiresTwoFactor, pendingToken }</c>.</summary>
    [HttpPost("admin/login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    public async Task<IActionResult> LoginStaff(LoginRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new LoginStaffCommand(body.Email ?? string.Empty, body.Password ?? string.Empty, ClientIp), cancellationToken);
        return FromResult(result, Ok);
    }

    /// <summary>Segunda etapa do 2FA: código recebido por e-mail.</summary>
    [HttpPost("admin/verify-otp")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    [ProducesResponseType<AuthResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyOtp(VerifyOtpRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new VerifyTwoFactorCommand(body.PendingToken ?? string.Empty, body.Code ?? string.Empty), cancellationToken);
        return FromResult(result, Ok);
    }

    /// <summary>Envia o link de redefinição. Resposta idêntica com ou sem conta.</summary>
    [HttpPost("admin/forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new RequestPasswordResetCommand(body.Email ?? string.Empty, ResetBaseUrl()), cancellationToken);
        return result.IsSuccess ? Ok(new { ok = true }) : Failure(result.Error);
    }

    /// <summary>Conclui a redefinição com o token do e-mail.</summary>
    [HttpPost("admin/reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new ResetPasswordCommand(body.Token ?? string.Empty, body.Password ?? string.Empty), cancellationToken);
        return result.IsSuccess ? Ok(new { ok = true }) : Failure(result.Error);
    }

    /// <summary>"Sair de todos os dispositivos": invalida todos os tokens já emitidos.</summary>
    [HttpPost("admin/revoke-sessions")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> RevokeSessions(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RevokeStaffSessionsCommand(CurrentUserId), cancellationToken);
        return result.IsSuccess ? Ok(new { ok = true }) : Failure(result.Error);
    }

    /// <summary>Suporte: gera o código de uso único para entrar como master de uma loja (auditado).</summary>
    [HttpPost("admin/impersonate/{tenantId:guid}")]
    [Authorize(Roles = Roles.SuperAdmin)]
    [ProducesResponseType<ImpersonationGrantResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Impersonate(Guid tenantId, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ImpersonateTenantCommand(tenantId, CurrentActor()), cancellationToken);
        return FromResult(result, Ok);
    }

    /// <summary>Troca o código de suporte por uma sessão de 15 minutos, no host da loja.</summary>
    [HttpPost("admin/impersonation/exchange")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingSetup.AuthPolicy)]
    [ProducesResponseType<AuthResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExchangeImpersonation(ImpersonationExchangeRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new ExchangeImpersonationCommand(body.Code ?? string.Empty), cancellationToken);
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
        var result = await sender.Send(new LoginCustomerCommand(body.Email ?? string.Empty, body.Password ?? string.Empty, ClientIp), cancellationToken);
        return FromResult(result, Ok);
    }

    /// <summary>O usuário do token atual (mesmo formato do Nest: <c>{ user: {...} }</c>).</summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var user = new Dictionary<string, object?>
        {
            ["sub"] = User.FindFirst("sub")?.Value,
            ["email"] = User.FindFirst("email")?.Value,
            ["name"] = User.FindFirst("name")?.Value,
            ["role"] = User.FindFirst("role")?.Value,
            ["tenantId"] = User.FindFirst("tenantId")?.Value,
        };
        if (int.TryParse(User.FindFirst("sessionVersion")?.Value, out var sessionVersion))
        {
            user["sessionVersion"] = sessionVersion;
        }

        return Ok(new { user });
    }

    /// <summary>Origem da loja visitada, para montar o link do e-mail (igual ao <c>resetBaseUrl</c> do Nest).</summary>
    private string ResetBaseUrl()
    {
        var header = Request.Headers["x-tenant-host"].ToString();
        var host = (header.Length > 0 ? header : Request.Host.Value ?? string.Empty).Trim();
        var isLocal = host.StartsWith("localhost", StringComparison.Ordinal) || host.StartsWith("127.", StringComparison.Ordinal)
            || host.Contains(".localhost", StringComparison.Ordinal);
        return $"{(isLocal ? "http" : "https")}://{host}";
    }
}
