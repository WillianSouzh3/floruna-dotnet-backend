using System.Diagnostics;
using Floruna.Application.Messaging;
using Floruna.SharedKernel;
using Microsoft.Extensions.Logging;

namespace Floruna.Application.Behaviors;

/// <summary>
/// Registra início, fim, duração e desfecho de cada request. É o behavior
/// mais externo, então mede o tempo do pipeline inteiro.
/// </summary>
/// <remarks>
/// Usa <c>[LoggerMessage]</c> (source generator): o template é compilado uma
/// vez e não há boxing nem formatação quando o nível está desligado. Nunca
/// registre o CONTEÚDO da request: pode conter PII (LGPD, RNF-11).
/// </remarks>
internal sealed partial class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var requestName = typeof(TRequest).Name;
        var started = Stopwatch.GetTimestamp();
        LogHandling(logger, requestName);

        var response = await next().ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (response is Result { IsFailure: true } failure)
        {
            LogFailed(logger, requestName, failure.Error.Code, elapsedMs);
        }
        else
        {
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
