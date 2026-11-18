using Floruna.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

/// <summary>
/// Mapeamento fica FORA da entidade (Fluent API): o domínio não ganha
/// atributos de banco. Isso é "persistence ignorance".
/// </summary>
internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Name).HasMaxLength(80).IsRequired();
        builder.Property(t => t.Slug)
            .HasConversion(slug => slug.Value, value => TenantSlug.FromPersistence(value))
            .HasMaxLength(40)
            .IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique();

        builder.Property(t => t.CustomDomain).HasMaxLength(253);
        builder.Property(t => t.CustomDomainToken).HasMaxLength(64);
        // Único só entre VERIFICADOS: um pedido pendente (de quem não provou posse)
        // não pode bloquear o dono real do domínio.
        builder.HasIndex(t => t.CustomDomain).IsUnique().HasFilter("custom_domain IS NOT NULL AND custom_domain_verified");

        builder.Property(t => t.ReferralCode).HasMaxLength(24).IsRequired();
        builder.HasIndex(t => t.ReferralCode).IsUnique();

        builder.Property(t => t.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(t => t.TimeZone).HasMaxLength(64).IsRequired();
        builder.Property(t => t.Locale).HasMaxLength(10).IsRequired();
    }
}
