using Floruna.Application.Abstractions;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Floruna.Infrastructure.Persistence;

public static partial class DatabaseInitializer
{
    /// <summary>Aplica migrations (se configurado) e cria a loja/admin de seed (idempotente).</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Floruna.Database");

        if (provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
        {
            await provider.GetRequiredService<FlorunaDbContext>().Database.MigrateAsync(cancellationToken);
            LogMigrated(logger);
        }

        var seed = provider.GetRequiredService<IOptions<SeedOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(seed.AdminEmail) && !string.IsNullOrWhiteSpace(seed.AdminPassword))
        {
            await SeedAsync(provider, seed, logger, cancellationToken);
        }
    }

    private static async Task SeedAsync(IServiceProvider provider, SeedOptions seed, ILogger logger, CancellationToken cancellationToken)
    {
        var db = provider.GetRequiredService<FlorunaDbContext>();
        var clock = provider.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();

        var slug = TenantSlug.FromPersistence(seed.TenantSlug);
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        if (tenant is null)
        {
            tenant = Tenant.Create(seed.TenantName, seed.TenantSlug, now).Value;
            db.Tenants.Add(tenant);
        }

        using (provider.GetRequiredService<TenantContext>()
            .Enter(new ResolvedTenant(tenant.Id, tenant.Slug.Value, tenant.Status, tenant.EffectivePlan(now))))
        {
            var email = Email.Create(seed.AdminEmail).Value;
            if (!await db.StaffUsers.AnyAsync(u => u.Email == email, cancellationToken))
            {
                var hash = provider.GetRequiredService<IPasswordHasher>().Hash(seed.AdminPassword!);
                db.StaffUsers.Add(StaffUser.Create(tenant.Id, seed.AdminName, email, hash, Roles.Master, now).Value);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        LogSeeded(logger, seed.TenantSlug);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrations aplicadas")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed garantido para a loja {Slug}")]
    private static partial void LogSeeded(ILogger logger, string slug);
}
