using Floruna.Application.Behaviors;
using Floruna.Application.Messaging;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Application;

/// <summary>
/// Cada camada registra os próprios serviços. O composition root (Api) só
/// chama <c>AddApplication()</c>, sem conhecer os detalhes internos.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddMessaging(assembly);

        // Ordem = ordem de execução (o primeiro é o mais externo).
        // Fase 1 acrescenta: Tenant, Idempotency e Transaction (Unit of Work).
        services.AddPipelineBehavior(typeof(LoggingBehavior<,>));
        services.AddPipelineBehavior(typeof(ValidationBehavior<,>));

        // Validators são stateless: singleton é seguro e evita recriá-los a cada request.
        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Singleton, includeInternalTypes: true);

        return services;
    }
}
