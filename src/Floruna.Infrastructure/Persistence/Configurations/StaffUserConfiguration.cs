using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class StaffUserConfiguration : IEntityTypeConfiguration<StaffUser>
{
    public void Configure(EntityTypeBuilder<StaffUser> builder)
    {
        builder.ToTable("staff_users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.Name).HasMaxLength(80).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(100).IsRequired();
        builder.Property(u => u.Role).HasMaxLength(20).IsRequired();
        builder.Property(u => u.Locale).HasMaxLength(10);

        // Mesmo e-mail pode existir em lojas diferentes; dentro de uma loja, não.
        builder.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();

        // FK para a loja: o banco também garante que não existe usuário órfão.
        builder.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
