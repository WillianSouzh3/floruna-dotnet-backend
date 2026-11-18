using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests.Support;

/// <summary>
/// Sobe a API de verdade em memória (TestServer), com o pipeline completo
/// de middlewares, filtros e DI. Só acrescenta o controller de sonda.
/// </summary>
public sealed class FlorunaApiFactory : WebApplicationFactory<Program>
{
    public const string AllowedOrigin = "http://loja.test:3000";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = AllowedOrigin,
        }));

        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(ContractProbeController).Assembly));
    }
}
