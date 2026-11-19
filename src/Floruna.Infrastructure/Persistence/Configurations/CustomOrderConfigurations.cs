using Floruna.Domain.CustomOrders;
using Floruna.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class CustomOrderRequestConfiguration : IEntityTypeConfiguration<CustomOrderRequest>
{
    public void Configure(EntityTypeBuilder<CustomOrderRequest> builder)
    {
        builder.ToTable("custom_order_requests");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.CustomerName).HasMaxLength(80).IsRequired();
        builder.Property(r => r.CustomerPhone).HasMaxLength(20).IsRequired();
        builder.Property(r => r.CustomerEmail).HasMaxLength(254).IsRequired();
        builder.Property(r => r.Occasion).HasMaxLength(20);
        builder.Property(r => r.ContainerType).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Size).HasMaxLength(10).IsRequired();
        builder.Property(r => r.BudgetRange).HasMaxLength(20);
        builder.Property(r => r.Ribbon).HasMaxLength(80).IsRequired();
        builder.Property(r => r.CardMessage).HasMaxLength(300).IsRequired();
        builder.Property(r => r.Notes).HasMaxLength(500).IsRequired();
        builder.Property(r => r.Status).HasMaxLength(10).IsRequired();
        builder.Property(r => r.AdminNotes).HasMaxLength(1000).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.Status, r.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomQuoteConfiguration : IEntityTypeConfiguration<CustomQuote>
{
    public void Configure(EntityTypeBuilder<CustomQuote> builder)
    {
        builder.ToTable("custom_quotes", t => t.HasCheckConstraint("ck_custom_quotes_total_not_negative", "total >= 0"));
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();
        builder.Property(q => q.Notes).HasMaxLength(1000).IsRequired();
        builder.Property(q => q.ResponseTokenHash).HasMaxLength(64).IsRequired();
        builder.Property(q => q.DecisionReason).HasMaxLength(300).IsRequired();
        builder.OwnsMany(q => q.Lines, l => l.ToJson("lines")); // a proposta é um documento imutável depois de publicada
        builder.Navigation(q => q.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(q => new { q.TenantId, q.RequestId, q.Version }).IsUnique(); // versões não se repetem
        // Um pedido é ligado a UM orçamento: a segunda conversão para o mesmo pedido bate no índice.
        builder.HasIndex(q => new { q.TenantId, q.ConvertedOrderId }).IsUnique().HasFilter("converted_order_id IS NOT NULL");
        builder.HasIndex(q => q.ValidUntil).HasFilter("state = 'Published'").HasDatabaseName("ix_custom_quotes_published_valid_until");
        builder.HasOne<CustomOrderRequest>().WithMany().HasForeignKey(q => q.RequestId).OnDelete(DeleteBehavior.Restrict);
        // Sem FK para orders: pedido nunca é apagado e a existência é checada no comando de conversão.
    }
}
