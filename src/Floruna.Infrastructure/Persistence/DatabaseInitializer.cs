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

    /// <summary>
    /// Modo "migrator" (<c>dotnet Floruna.Api.dll migrate</c>): aplica as migrations e sai. Na AWS roda
    /// como task avulsa do ECS ANTES de trocar as tasks da API, com o mesmo binário da versão nova.
    /// Duas execuções simultâneas não se atropelam: o EF Core 9+ trava a tabela de histórico.
    /// </summary>
    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        await db.Database.MigrateAsync(cancellationToken);
        LogMigrationsApplied(scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Floruna.Database"), pending.Count, string.Join(", ", pending));
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
            await EnsureUserAsync(seed.AdminEmail!, seed.AdminPassword!, seed.AdminName, Roles.Master);
            if (!string.IsNullOrWhiteSpace(seed.SuperAdminEmail) && !string.IsNullOrWhiteSpace(seed.SuperAdminPassword))
            {
                await EnsureUserAsync(seed.SuperAdminEmail, seed.SuperAdminPassword, "Suporte Floruna", Roles.SuperAdmin);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        LogSeeded(logger, seed.TenantSlug);

        async Task EnsureUserAsync(string rawEmail, string password, string name, string role)
        {
            var email = Email.Create(rawEmail).Value;
            if (!await db.StaffUsers.AnyAsync(u => u.Email == email, cancellationToken))
            {
                var hash = provider.GetRequiredService<IPasswordHasher>().Hash(password);
                db.StaffUsers.Add(StaffUser.Create(tenant.Id, name, email, hash, role, now).Value);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} migration(s) aplicada(s): {Names}")]
    private static partial void LogMigrationsApplied(ILogger logger, int count, string names);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrations aplicadas")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed garantido para a loja {Slug}")]
    private static partial void LogSeeded(ILogger logger, string slug);
}
