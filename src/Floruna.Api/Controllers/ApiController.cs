using System.Security.Claims;
using Floruna.Api.Contracts;
using Floruna.Domain.Audit;
using Floruna.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace Floruna.Api.Controllers;

/// <summary>
/// Base dos controllers da v1. O controller só traduz HTTP ↔ mensagem do
/// mediator ↔ HTTP; não tem regra de negócio.
/// </summary>
[ApiController]
[Produces("application/json")]
public abstract class ApiController : ControllerBase
{
    /// <summary>Converte um <see cref="Result"/> em resposta HTTP.</summary>
    protected IActionResult FromResult<T>(Result<T> result, Func<T, IActionResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : Failure(result.Error);
    }

    protected IActionResult FromResult(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? NoContent() : Failure(result.Error);
    }

    /// <summary>Resposta de erro no envelope do contrato. O envelope de sucesso NÃO é aplicado a ela.</summary>
    protected ObjectResult Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.RetryAfterSeconds is { } retryAfter)
        {
            Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ObjectResult(ApiResponses.Error(HttpContext, error))
        {
            StatusCode = ApiResponses.StatusCodeFor(error.Type),
        };
    }

    /// <summary>IP real do cliente (já corrigido pelo ForwardedHeaders atrás do load balancer).</summary>
    protected string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>Id do usuário do token.</summary>
    protected Guid CurrentUserId => Guid.Parse(User.FindFirstValue("sub")!);

    /// <summary>Quem está agindo, para a auditoria.</summary>
    protected AuditActor CurrentActor() => new(
        Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : null,
        User.FindFirstValue("role") ?? string.Empty,
        Guid.TryParse(User.FindFirstValue("impersonatedBy"), out var by) ? by : null,
        ClientIp ?? string.Empty);
}
