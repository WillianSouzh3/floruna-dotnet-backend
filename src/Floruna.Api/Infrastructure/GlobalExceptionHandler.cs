using Floruna.Api.Contracts;
using Floruna.SharedKernel;
using Microsoft.AspNetCore.Diagnostics;

namespace Floruna.Api.Infrastructure;

/// <summary>
/// Último recurso: exceção não tratada vira 500 no envelope de erro, sem
/// vazar stack trace. Erro de NEGÓCIO não chega aqui: ele é um Result.
/// </summary>
internal sealed partial class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // Erros do próprio Kestrel/ASP.NET (corpo grande, JSON malformado) trazem o status certo.
        var status = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;

        if (status >= 500)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }

        var error = new Error(ApiResponses.CodeForStatus(status), ApiResponses.MessageForStatus(status));
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(ApiResponses.Error(httpContext, error), cancellationToken).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Exceção não tratada em {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);
}
