using Floruna.Api.Contracts;
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
        return new ObjectResult(ApiResponses.Error(HttpContext, error))
        {
            StatusCode = ApiResponses.StatusCodeFor(error.Type),
        };
    }
}
