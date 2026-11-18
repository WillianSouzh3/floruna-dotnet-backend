using Floruna.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Floruna.Api.Infrastructure;

/// <summary>
/// Embrulha todo retorno de controller da v1 em <c>{ data, meta }</c>.
/// Equivale ao <c>ApiEnvelopeInterceptor</c> do Nest.
/// </summary>
/// <remarks>
/// Padrão Decorator aplicado ao pipeline do MVC: os controllers devolvem só
/// o dado (<c>Ok(produto)</c>) e o formato do contrato fica num lugar só.
/// </remarks>
internal sealed class ApiEnvelopeResultFilter(TimeProvider timeProvider) : IAsyncResultFilter
{
    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.Result is ObjectResult { Value: not ApiErrorEnvelope and not ApiSuccessEnvelope } objectResult
            && context.HttpContext.Request.Path.StartsWithSegments("/" + ApiVersion.RoutePrefix, StringComparison.OrdinalIgnoreCase))
        {
            objectResult.Value = new ApiSuccessEnvelope(objectResult.Value, ApiResponses.Meta(context.HttpContext, timeProvider));
            objectResult.DeclaredType = typeof(ApiSuccessEnvelope);
        }

        return next();
    }
}
