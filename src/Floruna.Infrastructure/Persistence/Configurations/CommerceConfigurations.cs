using Floruna.Domain.Delivery;
using Floruna.Domain.Ordering;
using Floruna.Domain.Platform;
using Floruna.Domain.Promotions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class CouponConfiguration : IEntityTypeConfiguration<Coupon>
{
    public void Configure(EntityTypeBuilder<Coupon> builder)
    {
        builder.ToTable("coupons", t => t.HasCheckConstraint("ck_coupons_used_within_limit", "max_uses IS NULL OR used_count <= max_uses"));
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Code).HasMaxLength(40).IsRequired();
        builder.Property(c => c.TargetEmail).HasMaxLength(254).IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.Code }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CouponRedemptionConfiguration : IEntityTypeConfiguration<CouponRedemption>
{
    public void Configure(EntityTypeBuilder<CouponRedemption> builder)
    {
        builder.ToTable("coupon_redemptions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Code).HasMaxLength(40).IsRequired();
        builder.Property(r => r.Email).HasMaxLength(254).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.CouponId, r.Email });
        builder.HasIndex(r => new { r.TenantId, r.OrderId });
        builder.HasOne<Coupon>().WithMany().HasForeignKey(r => r.CouponId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeliveryZoneConfiguration : IEntityTypeConfiguration<DeliveryZone>
{
    public void Configure(EntityTypeBuilder<DeliveryZone> builder)
    {
        builder.ToTable("delivery_zones", t => t.HasCheckConstraint("ck_delivery_zones_range", "zip_start <= zip_end"));
        builder.HasKey(z => z.Id);
        builder.Property(z => z.Id).ValueGeneratedNever();
        builder.Property(z => z.Name).HasMaxLength(120).IsRequired();
        builder.HasIndex(z => new { z.TenantId, z.IsActive, z.ZipStart, z.ZipEnd });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(z => z.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CapacityRuleConfiguration : IEntityTypeConfiguration<CapacityRule>
{
    public void Configure(EntityTypeBuilder<CapacityRule> builder)
    {
        builder.ToTable("delivery_capacity_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Note).HasMaxLength(200).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.Kind, r.Date, r.Weekday });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Contador de entregas aceitas por janela (escopo: zona da regra ou global = Guid.Empty).</summary>
internal sealed class DeliverySlotUsage
{
    public Guid TenantId { get; set; }

    public DateOnly Date { get; set; }

    public DeliveryWindow Window { get; set; }

    public Guid ZoneKey { get; set; }

    public int Used { get; set; }
}

internal sealed class DeliverySlotUsageConfiguration : IEntityTypeConfiguration<DeliverySlotUsage>
{
    public void Configure(EntityTypeBuilder<DeliverySlotUsage> builder)
    {
        builder.ToTable("delivery_slot_usage", t => t.HasCheckConstraint("ck_delivery_slot_usage_used", "used >= 0"));
        builder.HasKey(u => new { u.TenantId, u.Date, u.Window, u.ZoneKey });
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", t =>
        {
            t.HasCheckConstraint("ck_orders_total_positive", "total_cents > 0");
            t.HasCheckConstraint("ck_orders_discount_within_items", "discount_cents <= subtotal_cents");
        });
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.IdempotencyKey).HasMaxLength(120);
        builder.Property(o => o.CouponCode).HasMaxLength(40);
        builder.Property(o => o.AccessTokenHash).HasMaxLength(64).IsRequired();

        // Snapshots são DOCUMENTOS imutáveis do pedido: jsonb, lidos e gravados junto com ele.
        builder.OwnsOne(o => o.Customer, c => c.ToJson("customer"));
        builder.OwnsOne(o => o.Address, a => a.ToJson("address"));
        builder.OwnsOne(o => o.GiftCard, g => g.ToJson("gift_card"));
        builder.OwnsMany(o => o.Items, i => i.ToJson("items"));
        builder.OwnsMany(o => o.History, h => h.ToJson("status_history"));
        builder.OwnsMany(o => o.Payments, p => p.ToJson("payments"));
        builder.Navigation(o => o.Payments).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.OwnsMany(o => o.Substitutions, s => s.ToJson("substitutions"));
        builder.Navigation(o => o.Substitutions).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Property(o => o.Origin).HasMaxLength(10).IsRequired();
        builder.Property(o => o.PaymentMethod).HasMaxLength(20).IsRequired();
        builder.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(o => o.History).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(o => new { o.TenantId, o.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
        builder.HasIndex(o => new { o.TenantId, o.Status, o.CreatedAt });
        builder.HasIndex(o => new { o.TenantId, o.CustomerId, o.CreatedAt });
        builder.HasIndex(o => o.CashSessionId).HasFilter("cash_session_id IS NOT NULL"); // conferência do caixa
        // Job de abandonados (todas as lojas): só os pendentes, por prazo.
        builder.HasIndex(o => o.PaymentDeadline).HasFilter("status = 'Pendente'").HasDatabaseName("ix_orders_pending_payment_deadline");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(o => o.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
