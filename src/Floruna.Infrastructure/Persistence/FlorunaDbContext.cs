using System.Reflection;
using Floruna.Application.Abstractions;
using Floruna.Domain.Audit;
using Floruna.Domain.Catalog;
using Floruna.Domain.Customers;
using Floruna.Domain.Delivery;
using Floruna.Domain.Identity;
using Floruna.Domain.Inventory;
using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.Domain.Platform;
using Floruna.Domain.Promotions;
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

    public DbSet<StockItem> StockItems => Set<StockItem>();

    public DbSet<StockLot> StockLots => Set<StockLot>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    public DbSet<StockReservation> StockReservations => Set<StockReservation>();

    public DbSet<Coupon> Coupons => Set<Coupon>();

    public DbSet<CouponRedemption> CouponRedemptions => Set<CouponRedemption>();

    public DbSet<DeliveryZone> DeliveryZones => Set<DeliveryZone>();

    public DbSet<CapacityRule> CapacityRules => Set<CapacityRule>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<PaymentIntent> PaymentIntents => Set<PaymentIntent>();

    public DbSet<PaymentSettings> PaymentSettings => Set<PaymentSettings>();

    public DbSet<PaymentNotification> PaymentNotifications => Set<PaymentNotification>();

    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<Domain.Workshops.Workshop> Workshops => Set<Domain.Workshops.Workshop>();

    public DbSet<Domain.Workshops.WorkshopRegistration> WorkshopRegistrations => Set<Domain.Workshops.WorkshopRegistration>();

    public DbSet<Domain.Loyalty.LoyaltyProgram> LoyaltyPrograms => Set<Domain.Loyalty.LoyaltyProgram>();

    public DbSet<Domain.Loyalty.LoyaltyAccount> LoyaltyAccounts => Set<Domain.Loyalty.LoyaltyAccount>();

    public DbSet<Domain.Loyalty.LoyaltyEntry> LoyaltyEntries => Set<Domain.Loyalty.LoyaltyEntry>();

    internal DbSet<Configurations.DeliverySlotUsage> DeliverySlotUsage => Set<Configurations.DeliverySlotUsage>();

    internal DbSet<Outbox.OutboxMessage> OutboxMessages => Set<Outbox.OutboxMessage>();

    internal DbSet<Outbox.OutboxReceipt> OutboxReceipts => Set<Outbox.OutboxReceipt>();

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

    /// <summary>
    /// Transação explícita. Sem retry automático DE PROPÓSITO: repetir o bloco
    /// re-aplicaria regras em objetos de domínio já alterados em memória (ex.: tirar
    /// do lote duas vezes). Falha transitória vira erro; quem chama repete com a
    /// MESMA chave de idempotência (ver ADR 0004).
    /// </summary>
    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Database.CurrentTransaction is not null)
        {
            return await work(cancellationToken); // já dentro de uma transação: participa dela
        }

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await work(cancellationToken);
            if (result is SharedKernel.Result { IsFailure: true })
            {
                await transaction.RollbackAsync(cancellationToken);
                ChangeTracker.Clear(); // nada do que foi alterado em memória pode vazar para um próximo SaveChanges
                return result;
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            ChangeTracker.Clear();
            throw;
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
        configurationBuilder.Properties<OrderId>().HaveConversion<OrderIdConverter>();
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
