using System.Diagnostics;
using Floruna.Application.Messaging;
using Floruna.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Floruna.Application.Behaviors;

/// <summary>
/// Registra início, fim, duração e desfecho de cada request: log, SPAN (trace) e MÉTRICA. É o
/// behavior mais externo, então mede o tempo do pipeline inteiro (Decorator: o handler nem sabe).
/// </summary>
/// <remarks>
/// Usa <c>[LoggerMessage]</c> (source generator): o template é compilado uma
/// vez e não há boxing nem formatação quando o nível está desligado. Nunca
/// registre o CONTEÚDO da request: pode conter PII (LGPD, RNF-11).
/// </remarks>
internal sealed partial class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger, Telemetry.FlorunaMetrics metrics)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var requestName = typeof(TRequest).Name;
        using var activity = Telemetry.FlorunaTelemetry.Source.StartActivity(requestName);
        activity?.SetTag("floruna.request", requestName);
        var started = Stopwatch.GetTimestamp();
        LogHandling(logger, requestName);

        TResponse response;
        try
        {
            response = await next().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            metrics.Request(requestName, "exception", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (response is Result { IsFailure: true } failure)
        {
            // Erro de NEGÓCIO (estoque, cupom) não é falha do sistema: o span fica OK, com o código registrado.
            activity?.SetTag("floruna.error.code", failure.Error.Code);
            activity?.SetTag("floruna.error.type", failure.Error.Type.ToString());
            metrics.Request(requestName, failure.Error.Type.ToString().ToLowerInvariant(), elapsedMs);
            LogFailed(logger, requestName, failure.Error.Code, elapsedMs);
        }
        else
        {
            metrics.Request(requestName, "success", elapsedMs);
            LogHandled(logger, requestName, elapsedMs);
        }

        return response;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processando {RequestName}")]
    private static partial void LogHandling(ILogger logger, string requestName);

    [LoggerMessage(Level = LogLevel.Information, Message = "{RequestName} concluído em {ElapsedMs:0.0} ms")]
    private static partial void LogHandled(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{RequestName} falhou com {ErrorCode} em {ElapsedMs:0.0} ms")]
    private static partial void LogFailed(ILogger logger, string requestName, string errorCode, double elapsedMs);
}
