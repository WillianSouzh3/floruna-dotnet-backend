using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Delivery;
using Floruna.Application.Inventory;
using Floruna.Application.Loyalty;
using Floruna.Application.Ordering;
using Floruna.Application.Outbox;
using Floruna.Application.Payments;
using Floruna.Application.Promotions;
using Floruna.Domain.Audit;
using Floruna.Domain.Catalog;
using Floruna.Domain.Customers;
using Floruna.Domain.Delivery;
using Floruna.Domain.Identity;
using Floruna.Domain.Inventory;
using Floruna.Domain.Loyalty;
using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.Domain.Platform;
using Floruna.Domain.Promotions;
using Floruna.Infrastructure.Catalog;
using Floruna.Infrastructure.Dns;
using Floruna.Infrastructure.Inventory;
using Floruna.Infrastructure.Mail;
using Floruna.Infrastructure.Ordering;
using Floruna.Infrastructure.Outbox;
using Floruna.Infrastructure.Payments;
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
        services.AddSingleton<IEmailDelivery, SmtpEmailDelivery>();

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
        services.AddOptions<BackgroundJobsOptions>().Bind(configuration.GetSection(BackgroundJobsOptions.SectionName));
        services.AddOptions<MercadoPagoOptions>().Bind(configuration.GetSection(MercadoPagoOptions.SectionName));
        services.AddOptions<SecretsOptions>()
            .Bind(configuration.GetSection(SecretsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecretsOptions>, SecretsOptionsValidator>();
    }

    private static void AddPersistence(IServiceCollection services)
    {
        services.AddScoped<TenantStampInterceptor>();
        services.AddSingleton<DomainEventsToOutboxInterceptor>();
        services.AddDbContext<FlorunaDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString, npgsql => npgsql
                .MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TenantStampInterceptor>(), sp.GetRequiredService<DomainEventsToOutboxInterceptor>()));

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<FlorunaDbContext>());
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<IStaffUserRepository, StaffUserRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ISingleUseTokenRepository, SingleUseTokenRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();

        // Catálogo: escrita (repositório), leitura (queries) e cache da vitrine.
        services.AddScoped<ICatalogRepository, CatalogRepository>();
        services.AddScoped<CatalogQueries>();
        services.AddScoped<ICatalogQueries>(sp => sp.GetRequiredService<CatalogQueries>());
        services.AddScoped<ICatalogCache, HybridCatalogCache>();
        services.AddHybridCache();

        // Estoque: escrita com trava de linha, leitura por projeções e o job de expiração de reservas.
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<IInventoryQueries, InventoryQueries>();
        services.AddSingleton<ReservationExpiryJob>();

        // Checkout, pedidos, cupons, entrega e a outbox (com o processador e o job de abandonados).
        services.AddScoped<ICouponRepository, CouponRepository>();
        services.AddScoped<IDeliveryRepository, DeliveryRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<CommerceQueries>();
        services.AddScoped<ICouponQueries>(sp => sp.GetRequiredService<CommerceQueries>());
        services.AddScoped<IDeliveryQueries>(sp => sp.GetRequiredService<CommerceQueries>());
        services.AddScoped<IOrderQueries>(sp => sp.GetRequiredService<CommerceQueries>());
        services.AddScoped<IOutboxAdmin, OutboxAdmin>();

        // Pagamentos: repositório, cifra de segredos e o gateway com resiliência.
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IRefundRepository, RefundRepository>();
        services.AddScoped<Domain.Workshops.IWorkshopRepository, Workshops.WorkshopRepository>();
        services.AddScoped<Application.Workshops.IWorkshopQueries, Workshops.WorkshopQueries>();
        services.AddScoped<IRefundQueries, RefundQueries>();
        services.AddSingleton<RefundRetryJob>();

        // Fidelidade: repositório com trava da conta, leituras e o job de expiração.
        services.AddScoped<ILoyaltyRepository, Loyalty.LoyaltyRepository>();
        services.AddScoped<ILoyaltyQueries, Loyalty.LoyaltyQueries>();
        services.AddSingleton<Loyalty.LoyaltyExpiryJob>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddHttpClient<IPaymentGateway, MercadoPagoGateway>((sp, http) =>
                http.BaseAddress = sp.GetRequiredService<IOptions<MercadoPagoOptions>>().Value.BaseUrl)
            .AddStandardResilienceHandler(resilience =>
            {
                // Checkout não pode esperar muito: 8 s por tentativa, 20 s no total.
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                // O padrão retenta também o POST. Aqui é seguro: ele vai com X-Idempotency-Key
                // (sem a chave, seria preciso chamar Retry.DisableForUnsafeHttpMethods()).
                resilience.Retry.MaxRetryAttempts = 2;
            });
        services.AddSingleton<OutboxProcessor>();
        services.AddSingleton<AbandonedOrdersJob>();

        // Papel do processo (Bulkhead): com BackgroundJobs:Enabled=false o processo só atende
        // HTTP. Em produção, a MESMA imagem roda como "api" (jobs desligados) e como
        // "worker" (jobs ligados), escalando separadamente (dívida M5 do Nest). Os testes
        // desligam e disparam cada ciclo explicitamente, de forma determinística.
        // A opção é lida quando o job INICIA (não aqui): a configuração final só existe depois do Build.
        services.AddHostedService(sp => sp.GetRequiredService<ReservationExpiryJob>());
        services.AddHostedService(sp => sp.GetRequiredService<OutboxProcessor>());
        services.AddHostedService(sp => sp.GetRequiredService<AbandonedOrdersJob>());
        services.AddHostedService(sp => sp.GetRequiredService<Loyalty.LoyaltyExpiryJob>());
        services.AddHostedService(sp => sp.GetRequiredService<RefundRetryJob>());

        services.AddHealthChecks().AddDbContextCheck<FlorunaDbContext>(DatabaseHealthCheckName, tags: ["ready"]);
    }
}
