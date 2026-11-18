using System.Globalization;
using Floruna.SharedKernel;

namespace Floruna.Api.Contracts;

/// <summary>Monta envelopes e traduz erros de domínio para HTTP. Única fonte desse mapeamento.</summary>
public static class ApiResponses
{
    public static ApiMeta Meta(HttpContext httpContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // Mesmo formato do Date.toISOString() do JavaScript: milissegundos e "Z".
        var timestamp = timeProvider.GetUtcNow().UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        return new ApiMeta(httpContext.TraceIdentifier, timestamp, ApiVersion.Current);
    }

    public static ApiErrorEnvelope Error(HttpContext httpContext, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var timeProvider = httpContext.RequestServices.GetRequiredService<TimeProvider>();
        var issues = error.Issues.Count > 0 ? error.Issues : null;
        return new ApiErrorEnvelope(new ApiErrorBody(error.Code, error.Message, issues), Meta(httpContext, timeProvider));
    }

    public static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    /// <summary>
    /// Código genérico por status, para erros que não nasceram de um <see cref="SharedKernel.Error"/>
    /// (rota inexistente, corpo grande demais, exceção). Igual a <c>errorCodeForStatus</c> do Nest.
    /// </summary>
    public static string CodeForStatus(int status) => status switch
    {
        400 => "VALIDATION_ERROR",
        401 => "AUTHENTICATION_REQUIRED",
        403 => "FORBIDDEN",
        404 => "NOT_FOUND",
        409 => "CONFLICT",
        413 => "PAYLOAD_TOO_LARGE",
        429 => "RATE_LIMITED",
        >= 500 => "INTERNAL_ERROR",
        _ => "HTTP_ERROR",
    };

    public static string MessageForStatus(int status) => status switch
    {
        400 => "Dados inválidos.",
        401 => "Autenticação necessária.",
        403 => "Acesso negado.",
        404 => "Recurso não encontrado.",
        405 => "Método não permitido.",
        413 => "Conteúdo grande demais.",
        429 => "Muitas requisições. Tente novamente em instantes.",
        >= 500 => "Erro interno do servidor.",
        _ => "Erro na requisição.",
    };
}
