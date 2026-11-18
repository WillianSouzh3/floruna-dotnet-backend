using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Application.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registra o mediator e todos os handlers concretos encontrados nos assemblies.
    /// </summary>
    /// <remarks>
    /// Handlers são SCOPED: vivem uma requisição HTTP, como o DbContext que
    /// eles vão usar. Um handler singleton que recebesse um DbContext scoped
    /// seria uma "captive dependency": o mesmo DbContext servindo todas as
    /// requisições (bug clássico de entrevista). O DI do ASP.NET detecta isso
    /// em Development com ValidateScopes.
    /// </remarks>
    public static IServiceCollection AddMessaging(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        services.AddScoped<Mediator>();
        services.AddScoped<ISender>(sp => sp.GetRequiredService<Mediator>());
        services.AddScoped<IPublisher>(sp => sp.GetRequiredService<Mediator>());

        var handlerContracts = new[] { typeof(IRequestHandler<,>), typeof(INotificationHandler<>) };

        var registrations =
            from type in assemblies.SelectMany(a => a.DefinedTypes)
            where type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            from contract in type.ImplementedInterfaces
            where contract.IsGenericType && handlerContracts.Contains(contract.GetGenericTypeDefinition())
            select (contract, implementation: type.AsType());

        foreach (var (contract, implementation) in registrations)
        {
            services.AddScoped(contract, implementation);
        }

        return services;
    }

    /// <summary>
    /// Adiciona um behavior genérico aberto ao pipeline. A ORDEM de chamada
    /// define a ordem de execução: o primeiro adicionado é o mais externo.
    /// </summary>
    public static IServiceCollection AddPipelineBehavior(this IServiceCollection services, Type openBehaviorType)
    {
        ArgumentNullException.ThrowIfNull(openBehaviorType);
        if (!openBehaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException("Informe o tipo genérico aberto, ex.: typeof(LoggingBehavior<,>).", nameof(openBehaviorType));
        }

        services.AddScoped(typeof(IPipelineBehavior<,>), openBehaviorType);
        return services;
    }
}
