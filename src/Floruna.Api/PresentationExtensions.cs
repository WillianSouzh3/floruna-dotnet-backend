using Floruna.Api.Contracts;
using Floruna.Api.Controllers;
using Floruna.Api.Infrastructure;
using Floruna.Api.Security;
using Floruna.SharedKernel;
using Microsoft.AspNetCore.Authorization;
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
        services.AddHealthChecks(); // o check do banco é registrado pela Infrastructure

        services.AddFlorunaAuthentication();
        services.AddAuthorization();
        // SCOPED, não singleton: depende do ITenantContext (scoped). Um singleton
        // capturaria a loja da 1ª requisição para sempre (captive dependency).
        // O ValidateScopes do ambiente Development detecta isso na subida.
        services.AddScoped<IAuthorizationHandler, PlanFeatureHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();
        services.AddFlorunaRateLimiting(configuration);
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

        app.UseRouting();
        app.UseCors(FrontendCorsPolicy);

        // A loja precisa estar resolvida ANTES da autenticação: o token é conferido contra ela.
        app.UseMiddleware<TenantResolutionMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapOpenApi("/api/openapi/{documentName}.json");
        app.MapScalarApiReference("/api/docs", options => options.WithOpenApiRoutePattern("/api/openapi/{documentName}.json"));

        // Todo controller exige autenticação por padrão (como o JwtAuthGuard global do Nest);
        // rotas públicas declaram [AllowAnonymous]. Seguro por padrão: esquecer o atributo fecha, não abre.
        app.MapControllers().RequireAuthorization();
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
