using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class PaymentIntentConfiguration : IEntityTypeConfiguration<PaymentIntent>
{
    public void Configure(EntityTypeBuilder<PaymentIntent> builder)
    {
        builder.ToTable("payment_intents", t => t.HasCheckConstraint("ck_payment_intents_amount_positive", "amount_cents > 0"));
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(i => i.PixCode).HasMaxLength(600).IsRequired();
        builder.Property(i => i.ProviderPaymentId).HasMaxLength(64);
        builder.Property(i => i.ReconciliationIssue).HasMaxLength(300);

        // O livro-razão do pagamento é um documento append-only dentro da intenção.
        builder.OwnsMany(i => i.Transactions, t =>
        {
            t.ToJson("transactions");
            t.Ignore(x => x.Evidence);
        });
        builder.Navigation(i => i.Transactions).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(i => new { i.TenantId, i.OrderId }).IsUnique(); // uma intenção por pedido
        builder.HasIndex(i => new { i.TenantId, i.ProviderPaymentId }).HasFilter("provider_payment_id IS NOT NULL");
        builder.HasIndex(i => i.ReconciliationIssue).HasFilter("reconciliation_issue IS NOT NULL");
        builder.HasOne<Order>().WithMany().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentSettingsConfiguration : IEntityTypeConfiguration<PaymentSettings>
{
    public void Configure(EntityTypeBuilder<PaymentSettings> builder)
    {
        builder.ToTable("payment_settings");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasIndex(s => s.TenantId).IsUnique();
        builder.Property(s => s.PixKey).HasMaxLength(77).IsRequired();
        builder.Property(s => s.PixReceiverName).HasMaxLength(60).IsRequired();
        builder.Property(s => s.PixCity).HasMaxLength(60).IsRequired();
        builder.Property(s => s.MercadoPagoTokenCiphertext).HasMaxLength(600);
        builder.HasOne<Tenant>().WithOne().HasForeignKey<PaymentSettings>(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PaymentNotificationConfiguration : IEntityTypeConfiguration<PaymentNotification>
{
    public void Configure(EntityTypeBuilder<PaymentNotification> builder)
    {
        builder.ToTable("payment_notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.Provider).HasMaxLength(32).IsRequired();
        builder.Property(n => n.ProviderPaymentId).HasMaxLength(64).IsRequired();
        builder.HasIndex(n => new { n.TenantId, n.ProviderPaymentId, n.ReceivedAt });
    }
}
