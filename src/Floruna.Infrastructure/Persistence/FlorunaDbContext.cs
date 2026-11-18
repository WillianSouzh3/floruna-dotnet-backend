using System.Reflection;
using Floruna.Application.Abstractions;
using Floruna.Domain.Audit;
using Floruna.Domain.Catalog;
using Floruna.Domain.Customers;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Floruna.Infrastructure.Persistence;

/// <summary>
/// O DbContext JÁ É um Unit of Work (rastreia mudanças e grava tudo num
/// SaveChanges) e cada DbSet já é um repositório genérico. Mesmo assim, o
/// domínio só enxerga as interfaces <see cref="IUnitOfWork"/> e os
/// repositórios por agregado, que expõem só o que o negócio precisa.
/// </summary>
public sealed class FlorunaDbContext(DbContextOptions<FlorunaDbContext> options, ITenantContext tenantContext)
    : DbContext(options), IUnitOfWork
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<SingleUseToken> SingleUseTokens => Set<SingleUseToken>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Collection> Collections => Set<Collection>();

    /// <summary>Contadores de tentativas de login. Global (a chave já embute a loja).</summary>
    internal DbSet<Security.LoginAttemptBucket> LoginAttempts => Set<Security.LoginAttemptBucket>();

    /// <summary>
    /// Lido pelo EF a CADA consulta (vira parâmetro SQL). Sem loja no
    /// contexto, lança <see cref="TenantContextMissingException"/>: o
    /// esquecimento vira erro, nunca vazamento entre lojas (fail-closed).
    /// </summary>
    internal TenantId CurrentTenantId => tenantContext.RequiredTenantId;

    internal ITenantContext TenantContext => tenantContext;

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            // Tradução da linguagem do banco para a da aplicação.
            throw new UniqueConstraintViolationException(pg.ConstraintName ?? "unknown", ex);
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // IDs fortemente tipados viram uuid; enums viram texto legível.
        configurationBuilder.Properties<TenantId>().HaveConversion<TenantIdConverter>();
        configurationBuilder.Properties<StaffUserId>().HaveConversion<StaffUserIdConverter>();
        configurationBuilder.Properties<CustomerId>().HaveConversion<CustomerIdConverter>();
        configurationBuilder.Properties<ProductId>().HaveConversion<ProductIdConverter>();
        configurationBuilder.Properties<CategoryId>().HaveConversion<CategoryIdConverter>();
        configurationBuilder.Properties<CollectionId>().HaveConversion<CollectionIdConverter>();
        configurationBuilder.Properties<VariantId>().HaveConversion<VariantIdConverter>().HaveMaxLength(28);
        configurationBuilder.Properties<Email>().HaveConversion<EmailConverter>().HaveMaxLength(Email.MaxLength);
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FlorunaDbContext).Assembly);

        foreach (var clrType in modelBuilder.Model.GetEntityTypes().Select(e => e.ClrType).ToList())
        {
            if (typeof(IAggregateRoot).IsAssignableFrom(clrType))
            {
                // Eventos de domínio são estado em memória, não coluna.
                modelBuilder.Entity(clrType).Ignore(nameof(IAggregateRoot.DomainEvents));
            }

            if (typeof(ITenantOwned).IsAssignableFrom(clrType))
            {
                ApplyTenantFilterMethod.MakeGenericMethod(clrType).Invoke(this, [modelBuilder]);
            }
        }
    }

    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(FlorunaDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// Filtro global: toda consulta a uma entidade de loja ganha
    /// <c>WHERE tenant_id = @tenant</c> automaticamente. Equivale ao
    /// <c>tenant-scope.plugin.ts</c> do Nest. Escape consciente:
    /// <c>IgnoreQueryFilters()</c> (só em código de plataforma).
    /// </summary>
    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantOwned
    {
        // Índices de cada entidade começam por tenant_id (ver as configurações):
        // um índice só de tenant_id seria redundante.
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.TenantId == CurrentTenantId);
    }
}
