using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // SINGLETON via DI (não o Singleton "static Instance" do GoF):
        // TimeProvider.System não tem estado, então uma instância serve o
        // processo inteiro. Por ser injetado, os testes trocam por um
        // FakeTimeProvider e controlam "agora" (cutoff de entrega, validade
        // de lote, expiração de reserva). O domínio NUNCA chama DateTime.Now.
        services.AddSingleton(TimeProvider.System);

        // Fase 1: DbContext (PostgreSQL), interceptors de tenant e outbox, repositórios.
        return services;
    }
}
