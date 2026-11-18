using Floruna.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Floruna.Api.IntegrationTests.Support;

/// <summary>
/// Sobe a API de verdade em memória (TestServer) contra um PostgreSQL REAL
/// num container descartável. Nada de banco em memória fingindo ser
/// Postgres: índices únicos, FKs e transações se comportam como em produção.
/// </summary>
public sealed class FlorunaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AllowedOrigin = "http://loja.test:3000";
    public const string JwtSecret = "integration-tests-secret-0123456789abcdef";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = AllowedOrigin,
            ["ConnectionStrings:Floruna"] = ConnectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["Jwt:Secret"] = JwtSecret,
            [$"{RateLimitingSettings.SectionName}:{nameof(RateLimitingSettings.AuthPermitLimit)}"] = "10000",
        }));

        builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(ContractProbeController).Assembly));
    }
}

/// <summary>
/// Todas as classes de integração compartilham UM container e rodam em
/// sequência (mesma collection): evita migrations concorrentes no mesmo banco.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<FlorunaApiFactory>
{
    public const string Name = "api";
}
