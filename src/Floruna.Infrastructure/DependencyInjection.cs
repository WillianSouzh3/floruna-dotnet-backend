using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Domain.Audit;
using Floruna.Domain.Catalog;
using Floruna.Domain.Customers;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Catalog;
using Floruna.Infrastructure.Dns;
using Floruna.Infrastructure.Mail;
using Floruna.Infrastructure.Persistence;
using Floruna.Infrastructure.Persistence.Repositories;
using Floruna.Infrastructure.Security;
using Floruna.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Floruna.Infrastructure;

public static class DependencyInjection
{
    public const string DatabaseHealthCheckName = "database";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // SINGLETON via DI (não o Singleton "static Instance" do GoF): sem
        // estado, uma instância serve o processo. Injetado, os testes trocam
        // por FakeTimeProvider. O domínio NUNCA chama DateTime.Now.
        services.AddSingleton(TimeProvider.System);

        AddOptions(services, configuration);
        AddPersistence(services);

        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddMemoryCache();
        services.AddSingleton<ITenantResolver, TenantHostResolver>();

        services.AddScoped<ILoginAttemptGuard, LoginAttemptGuard>();
        services.AddSingleton<ICredentialHasher, CredentialHasher>();
        services.AddSingleton<IDnsTxtResolver, DnsTxtResolver>();

        // E-mail: a fila é singleton (compartilhada entre requisições) e exposta como
        // IEmailSender; o despachante é um hosted service que a esvazia.
        services.AddSingleton<IEmailTransport, SmtpEmailTransport>();
        services.AddSingleton<BackgroundEmailQueue>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<BackgroundEmailQueue>());
        services.AddHostedService<EmailDispatcher>();

        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Configure(o =>
            {
                o.ConnectionString = configuration.GetConnectionString("Floruna") ?? string.Empty;
                o.MigrateOnStartup = configuration.GetValue<bool>("Database:MigrateOnStartup");
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            // Compatibilidade com o .env do Nest: AUTH_SECRET continua valendo.
            .PostConfigure(o => o.Secret = string.IsNullOrWhiteSpace(o.Secret) ? configuration["AUTH_SECRET"] ?? string.Empty : o.Secret)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PlatformSettings>().Bind(configuration.GetSection(PlatformSettings.SectionName));
        services.AddOptions<AuthenticationSettings>().Bind(configuration.GetSection(AuthenticationSettings.SectionName));
        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName));
        services.AddOptions<SmtpSettings>().Bind(configuration.GetSection(SmtpSettings.SectionName));
    }

    private static void AddPersistence(IServiceCollection services)
    {
        services.AddScoped<TenantStampInterceptor>();
        services.AddDbContext<FlorunaDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history")
                .EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TenantStampInterceptor>()));

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<FlorunaDbContext>());
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IStaffUserRepository, StaffUserRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ISingleUseTokenRepository, SingleUseTokenRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();

        // Catálogo: escrita (repositório), leitura (queries) e cache da vitrine.
        services.AddScoped<ICatalogRepository, CatalogRepository>();
        services.AddScoped<ICatalogQueries, CatalogQueries>();
        services.AddScoped<ICatalogCache, HybridCatalogCache>();
        services.AddHybridCache();

        services.AddHealthChecks().AddDbContextCheck<FlorunaDbContext>(DatabaseHealthCheckName, tags: ["ready"]);
    }
}
