using Floruna.Application.Abstractions;
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
        services.AddPipelineBehavior(typeof(LoggingBehavior<,>));
        services.AddPipelineBehavior(typeof(ValidationBehavior<,>));

        // Validators são stateless: singleton é seguro e evita recriá-los a cada request.
        // ProductInputValidator é composto dentro dos validators de criar/atualizar (recebe um
        // parâmetro), então fica fora do registro automático.
        services.AddValidatorsFromAssembly(
            assembly,
            ServiceLifetime.Singleton,
            filter: scan => scan.ValidatorType != typeof(Catalog.ProductInputValidator),
            includeInternalTypes: true);

        // Contexto da loja: UMA instância por requisição, exposta por dois tipos
        // (a interface só-leitura para todos, a classe concreta para quem preenche).
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        // Serviço de aplicação compartilhado entre a cotação de entrega e o checkout.
        services.AddScoped<Delivery.DeliveryResolver>();
        services.AddScoped<Payments.PaymentInitializer>();
        services.AddScoped<Loyalty.CheckoutLoyalty>();
        services.AddScoped<Payments.RefundSaga>();
        services.AddScoped<Ordering.OrderItemPricer>();
        services.AddScoped<Payments.PaidOrderCancellation>();
        services.AddScoped<Payments.PaymentConfirmation>();

        return services;
    }
}
