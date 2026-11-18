using Floruna.Domain.Catalog;
using Floruna.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("stock_items", t =>
        {
            // RN-20 também no banco: saldo e reserva nunca negativos, reserva nunca maior que o físico.
            t.HasCheckConstraint("ck_stock_items_on_hand_non_negative", "on_hand >= 0");
            t.HasCheckConstraint("ck_stock_items_reserved_valid", "reserved >= 0 AND reserved <= on_hand");
        });
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("product_id").ValueGeneratedNever();
        builder.HasOne<Product>().WithOne().HasForeignKey<StockItem>(i => i.Id).OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(i => i.Lots).WithOne().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Lots).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class StockLotConfiguration : IEntityTypeConfiguration<StockLot>
{
    public void Configure(EntityTypeBuilder<StockLot> builder)
    {
        builder.ToTable("stock_lots", t =>
            t.HasCheckConstraint("ck_stock_lots_remaining_valid", "remaining >= 0 AND remaining <= initial_quantity"));
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Note).HasMaxLength(200).IsRequired();
        builder.Property(l => l.ReceivedBy).HasMaxLength(120).IsRequired();
        // Índice parcial: só os lotes com saldo, que são os consultados (FEFO, vencimento).
        builder.HasIndex(l => new { l.TenantId, l.ProductId, l.ExpiresAt }).HasFilter("remaining > 0");
        builder.HasIndex(l => new { l.TenantId, l.ExpiresAt }).HasFilter("remaining > 0 AND expires_at IS NOT NULL");
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.ToTable("stock_movements");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.ProductName).HasMaxLength(120).IsRequired();
        builder.Property(m => m.Note).HasMaxLength(200).IsRequired();
        builder.Property(m => m.By).HasMaxLength(120).IsRequired();
        builder.Property(m => m.IdempotencyKey).HasMaxLength(120);
        // Sem FK para o produto: o ledger é histórico e sobrevive à exclusão do produto.
        builder.HasIndex(m => new { m.TenantId, m.ProductId, m.CreatedAt });
        builder.HasIndex(m => new { m.TenantId, m.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");

        builder.OwnsMany(m => m.Lots, lot =>
        {
            lot.ToTable("stock_movement_lots");
            lot.WithOwner().HasForeignKey("movement_id");
            lot.HasKey("movement_id", nameof(StockMovementLot.LotId));
        });
        builder.Navigation(m => m.Lots).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> builder)
    {
        builder.ToTable("stock_reservations", t => t.HasCheckConstraint("ck_stock_reservations_quantity_positive", "quantity > 0"));
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.OwnerId).HasMaxLength(64).IsRequired();
        builder.Property(r => r.IdempotencyKey).HasMaxLength(120).IsRequired();
        builder.Property(r => r.Note).HasMaxLength(200).IsRequired();
        builder.HasIndex(r => new { r.TenantId, r.IdempotencyKey }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.OwnerType, r.OwnerId });
        // O job varre TODAS as lojas pelas reservas vencidas: índice global e parcial.
        builder.HasIndex(r => r.ExpiresAt).HasFilter("state = 'Held'").HasDatabaseName("ix_stock_reservations_held_expires_at");
        builder.HasOne<StockItem>().WithMany().HasForeignKey(r => r.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}
