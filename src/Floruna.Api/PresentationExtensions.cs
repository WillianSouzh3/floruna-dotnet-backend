using Floruna.Api.Contracts;
using Floruna.Api.Controllers;
using Floruna.Api.Infrastructure;
using Floruna.SharedKernel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

namespace Floruna.Api;

internal static class PresentationExtensions
{
    private const string FrontendCorsPolicy = "frontend";

    public static IServiceCollection AddPresentation(this IServiceCollection services, IConfiguration configuration)
    {
        // Options Pattern: bind + DataAnnotations + validador customizado + falha NA SUBIDA.
        services.AddOptions<CorsSettings>()
            .Bind(configuration.GetSection(CorsSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CorsSettings>, CorsSettingsValidator>();

        services.AddCors();
        services.AddSingleton<IConfigureOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>, ConfigureFrontendCors>();

        services
            .AddControllers(options => options.Filters.Add<ApiEnvelopeResultFilter>())
            .ConfigureApiBehaviorOptions(options =>
                // Erro de binding/model state no mesmo formato do ValidationPipe do Nest.
                options.InvalidModelStateResponseFactory = context =>
                {
                    var issues = context.ModelState.Values
                        .SelectMany(v => v.Errors)
                        .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Valor inválido." : e.ErrorMessage)
                        .ToList();
                    var error = Error.Validation("VALIDATION_ERROR", "Dados inválidos.", issues);
                    return new BadRequestObjectResult(ApiResponses.Error(context.HttpContext, error));
                });

        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();
        services.AddHealthChecks(); // Fase 1: .AddNpgSql(..., name: HealthController.DatabaseCheckName, tags: ["ready"])
        services.AddSingleton<ProcessInfo>();
        services.AddOpenApi("v1");

        return services;
    }

    public static WebApplication UsePresentation(this WebApplication app)
    {
        app.UseMiddleware<RequestContextMiddleware>();
        app.UseExceptionHandler();

        // Respostas sem corpo (rota inexistente 404, método 405) ganham o envelope de erro.
        app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;
            var status = http.Response.StatusCode;
            var error = new Error(ApiResponses.CodeForStatus(status), ApiResponses.MessageForStatus(status));
            await http.Response.WriteAsJsonAsync(ApiResponses.Error(http, error)).ConfigureAwait(false);
        });

        app.UseCors(FrontendCorsPolicy);

        app.MapOpenApi("/api/openapi/{documentName}.json");
        app.MapScalarApiReference("/api/docs", options => options.WithOpenApiRoutePattern("/api/openapi/{documentName}.json"));

        app.MapControllers();
        return app;
    }

    /// <summary>Lê as origens das <see cref="CorsSettings"/> já validadas.</summary>
    private sealed class ConfigureFrontendCors(IOptions<CorsSettings> settings)
        : IConfigureOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>
    {
        public void Configure(Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions options) =>
            options.AddPolicy(FrontendCorsPolicy, policy => policy
                .WithOrigins([.. settings.Value.AllowedOrigins])
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials());
    }
}
