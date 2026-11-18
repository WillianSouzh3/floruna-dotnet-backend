using Floruna.Domain.Catalog;
using Floruna.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", t =>
        {
            // O banco também protege as invariantes de dinheiro (defesa em profundidade).
            t.HasCheckConstraint("ck_products_price_positive", "price_cents > 0");
            t.HasCheckConstraint("ck_products_discount_below_price", "discount_price_cents IS NULL OR discount_price_cents < price_cents");
            t.HasCheckConstraint("ck_products_stock_non_negative", "stock >= 0");
        });
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Slug).HasConversion(s => s.Value, v => CatalogSlug.FromPersistence(v)).HasMaxLength(CatalogSlug.MaxLength);
        builder.Property(p => p.Name).HasMaxLength(120).IsRequired();
        builder.Property(p => p.ShortDescription).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Description).IsRequired();
        builder.Property(p => p.Sku).HasMaxLength(40).IsRequired();
        builder.Property(p => p.Barcode).HasMaxLength(14).IsRequired();
        builder.Property(p => p.Images).IsRequired(); // text[] no PostgreSQL

        // As propriedades Money são calculadas a partir das colunas em centavos.
        builder.Ignore(p => p.Price);
        builder.Ignore(p => p.DiscountPrice);
        builder.Ignore(p => p.Cost);
        builder.Ignore(p => p.EffectivePrice);

        builder.HasIndex(p => new { p.TenantId, p.Slug }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.Sku }).IsUnique().HasFilter("sku <> ''");
        builder.HasIndex(p => new { p.TenantId, p.Barcode }).IsUnique().HasFilter("barcode <> ''");
        builder.HasIndex(p => new { p.TenantId, p.IsActive, p.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);

        // Variações e vínculos com categorias pertencem ao produto (owned): nascem,
        // mudam e morrem com ele, sempre através da raiz do agregado.
        builder.OwnsMany(p => p.Variants, variant =>
        {
            variant.ToTable("product_variants");
            variant.WithOwner().HasForeignKey("product_id");
            variant.HasKey(v => v.Id);
            variant.Property(v => v.Id).ValueGeneratedNever();
            variant.Property(v => v.Label).HasMaxLength(40).IsRequired();
            variant.Property(v => v.Sku).HasMaxLength(40);
            variant.Ignore(v => v.Price);
        });
        builder.Navigation(p => p.Variants).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(p => p.Categories, link =>
        {
            link.ToTable("product_categories");
            link.WithOwner().HasForeignKey("product_id");
            link.HasKey("product_id", nameof(ProductCategoryLink.CategoryId));
            link.HasIndex(l => l.CategoryId);
        });
        builder.Navigation(p => p.Categories).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Slug).HasConversion(s => s.Value, v => CatalogSlug.FromPersistence(v)).HasMaxLength(CatalogSlug.MaxLength);
        builder.Property(c => c.Name).HasMaxLength(120).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(500).IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.Slug }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CollectionConfiguration : IEntityTypeConfiguration<Collection>
{
    public void Configure(EntityTypeBuilder<Collection> builder)
    {
        builder.ToTable("collections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Slug).HasConversion(s => s.Value, v => CatalogSlug.FromPersistence(v)).HasMaxLength(CatalogSlug.MaxLength);
        builder.Property(c => c.Name).HasMaxLength(120).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(500).IsRequired();
        builder.HasIndex(c => new { c.TenantId, c.Slug }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);

        builder.OwnsMany(c => c.Items, item =>
        {
            item.ToTable("collection_items");
            item.WithOwner().HasForeignKey("collection_id");
            item.HasKey("collection_id", nameof(CollectionItem.ProductId));
            // Apagar um produto tira ele de todas as coleções (o banco garante).
            item.HasOne<Product>().WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Navigation(c => c.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
