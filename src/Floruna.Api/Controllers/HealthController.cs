using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Floruna.Api.Controllers;

/// <summary>Formato idêntico ao <c>HealthController</c> do Nest.</summary>
public sealed record HealthSnapshot(
    string Status,
    string Db,
    HealthChecksSnapshot Checks,
    string Version,
    double Uptime,
    string Timestamp);

public sealed record HealthChecksSnapshot(string Process, string Database);

/// <summary>
/// Health Endpoint Monitoring (padrão de nuvem). O ALB/ECS da AWS usa
/// <c>live</c> para saber se reinicia a task e <c>ready</c> para saber se
/// manda tráfego para ela.
/// </summary>
[AllowAnonymous]
[Route(Contracts.ApiVersion.RoutePrefix + "/health")]
public sealed class HealthController(HealthCheckService healthChecks, ProcessInfo processInfo, TimeProvider timeProvider)
    : ApiController
{
    public const string DatabaseCheckName = Floruna.Infrastructure.DependencyInjection.DatabaseHealthCheckName;

    /// <summary>Liveness: o processo responde. Não consulta dependências.</summary>
    [HttpGet("live")]
    [ProducesResponseType<HealthSnapshot>(StatusCodes.Status200OK)]
    public IActionResult Live() => Ok(Snapshot("ok", database: "up"));

    /// <summary>Readiness: as dependências (banco) respondem.</summary>
    [HttpGet("ready")]
    [ProducesResponseType<HealthSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthSnapshot>(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Ready(CancellationToken cancellationToken) => ReadinessAsync(cancellationToken);

    /// <summary>Alias compatível do readiness.</summary>
    [HttpGet]
    [ProducesResponseType<HealthSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthSnapshot>(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Check(CancellationToken cancellationToken) => ReadinessAsync(cancellationToken);

    private async Task<IActionResult> ReadinessAsync(CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(
            check => check.Tags.Contains("ready"), cancellationToken).ConfigureAwait(false);

        var database = report.Entries.TryGetValue(DatabaseCheckName, out var entry)
            ? (entry.Status == HealthStatus.Healthy ? "up" : "down")
            : "not_configured";

        var ready = report.Status == HealthStatus.Healthy;
        var snapshot = Snapshot(ready ? "ok" : "not_ready", database);
        return ready ? Ok(snapshot) : StatusCode(StatusCodes.Status503ServiceUnavailable, snapshot);
    }

    private HealthSnapshot Snapshot(string status, string database)
    {
        var now = timeProvider.GetUtcNow();
        return new HealthSnapshot(
            status,
            database,
            new HealthChecksSnapshot("up", database),
            processInfo.Version,
            Math.Round((now - processInfo.StartedAt).TotalSeconds, 3),
            now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Dados imutáveis do processo, registrados como SINGLETON: uma instância,
/// compartilhada por todas as requisições.
/// </summary>
/// <remarks>
/// Singletons do DI são criados na PRIMEIRA resolução, não na subida. Por
/// isso o início vem do sistema operacional e não de "agora": senão o
/// uptime contaria a partir da primeira chamada ao health.
/// </remarks>
public sealed class ProcessInfo(IConfiguration configuration)
{
    public DateTimeOffset StartedAt { get; } = ReadProcessStart();

    /// <summary>SHA do commit, injetado pelo CI na imagem (<c>GIT_SHA</c>).</summary>
    public string Version { get; } = configuration["GIT_SHA"] ?? "dev";

    private static DateTimeOffset ReadProcessStart()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }
}
