using Floruna.Application.Telemetry;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Floruna.Api.Infrastructure;

/// <summary>
/// OpenTelemetry: traces (HTTP de entrada e de saída, PostgreSQL, mediator, outbox) e métricas (HTTP,
/// runtime, negócio). A coleta é SEMPRE ligada; a EXPORTAÇÃO só quando há destino configurado
/// (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>): Aspire Dashboard local, coletor ADOT na AWS (X-Ray + CloudWatch).
/// O código não conhece o destino: trocar de fornecedor é configuração.
/// </summary>
internal static class ObservabilitySetup
{
    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: configuration["OTEL_SERVICE_NAME"] ?? "floruna-api",
                    serviceVersion: configuration["GIT_SHA"] ?? "dev")
                .AddAttributes([new("deployment.environment.name", configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production")]))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new DropOrphanClientSpans()))
                .AddSource(FlorunaTelemetry.Name)
                .AddAspNetCoreInstrumentation(options =>
                    // Health check do ALB a cada 15 s, por task: ruído puro no painel de traces.
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/api/v1/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddMeter(FlorunaTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddNpgsqlInstrumentation());

        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return services;
    }
}

/// <summary>
/// Os jobs consultam o banco a cada 2 s ("tem mensagem na outbox?"). Sem pai, cada consulta viraria um
/// trace sozinho: ruído no painel e custo no X-Ray para registrar "nada aconteceu". Regra: span de SAÍDA
/// (banco, HTTP) sem pai é descartado. Trabalho de verdade (requisição, comando, mensagem da outbox) sempre
/// tem span pai, então continua inteiro no trace.
/// </summary>
internal sealed class DropOrphanClientSpans : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.Kind == System.Diagnostics.ActivityKind.Client
            ? new SamplingResult(SamplingDecision.Drop)
            : new SamplingResult(SamplingDecision.RecordAndSample);
}
