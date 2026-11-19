using System.Text.RegularExpressions;
using Floruna.Api.Contracts;

namespace Floruna.Api.Infrastructure;

/// <summary>
/// Correlação e cabeçalhos comuns a toda resposta.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>X-Request-ID</c>: reaproveita o ID enviado pelo BFF (para seguir a mesma
/// requisição no log do Next e da API) se for seguro; senão gera um novo.</item>
/// <item><c>X-API-Version</c>: igual ao Nest.</item>
/// <item>Cabeçalhos de segurança básicos (o equivalente mínimo do helmet).</item>
/// </list>
/// </remarks>
internal sealed partial class RequestContextMiddleware(RequestDelegate next)
{
    public const string RequestIdHeader = "X-Request-ID";

    public const string TraceIdHeader = "X-Trace-Id";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var incoming = context.Request.Headers[RequestIdHeader].ToString();
        var requestId = SafeRequestId().IsMatch(incoming) ? incoming : Guid.CreateVersion7().ToString();
        context.TraceIdentifier = requestId;

        var headers = context.Response.Headers;
        headers[RequestIdHeader] = requestId;

        // O ID do trace volta para o cliente: o suporte recebe esse código e acha o caminho INTEIRO da
        // requisição (incluindo o que a outbox fez depois) no painel de traces.
        if (System.Diagnostics.Activity.Current is { } activity)
        {
            headers[TraceIdHeader] = activity.TraceId.ToString();
            activity.SetTag("floruna.request_id", requestId);
        }

        headers["X-API-Version"] = ApiVersion.Current;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";

        return next(context);
    }

    // Aceita só caracteres seguros para log/header (evita log injection).
    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex SafeRequestId();
}
