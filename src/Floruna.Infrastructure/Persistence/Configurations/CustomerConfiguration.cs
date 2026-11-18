using Floruna.Domain.Customers;
using Floruna.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Name).HasMaxLength(80).IsRequired();
        builder.Property(c => c.PasswordHash).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Phone).HasMaxLength(30).IsRequired();

        builder.HasIndex(c => new { c.TenantId, c.Email }).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
