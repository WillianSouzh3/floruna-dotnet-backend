using Floruna.Domain.Platform;
using Floruna.Domain.Workshops;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class WorkshopConfiguration : IEntityTypeConfiguration<Workshop>
{
    public void Configure(EntityTypeBuilder<Workshop> builder)
    {
        builder.ToTable("workshops", t =>
        {
            // Segunda barreira: mesmo um bug no código não consegue vender a vaga 11 de 10.
            t.HasCheckConstraint("ck_workshops_registered_within_capacity", "registered >= 0 AND registered <= capacity");
            t.HasCheckConstraint("ck_workshops_price_not_negative", "price_cents >= 0");
        });
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).ValueGeneratedNever();
        builder.Property(w => w.Slug).HasMaxLength(60).IsRequired();
        builder.Property(w => w.Title).HasMaxLength(120).IsRequired();
        builder.Property(w => w.ShortDescription).HasMaxLength(200).IsRequired();
        builder.Property(w => w.Description).HasMaxLength(5_000).IsRequired();
        builder.Property(w => w.Date).HasMaxLength(60).IsRequired();
        builder.Property(w => w.Location).HasMaxLength(200).IsRequired();
        builder.Property(w => w.Image).HasMaxLength(500).IsRequired();
        builder.Ignore(w => w.SeatsLeft);
        builder.HasIndex(w => new { w.TenantId, w.Slug }).IsUnique();
        builder.HasIndex(w => new { w.TenantId, w.IsActive, w.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(w => w.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkshopRegistrationConfiguration : IEntityTypeConfiguration<WorkshopRegistration>
{
    public void Configure(EntityTypeBuilder<WorkshopRegistration> builder)
    {
        builder.ToTable("workshop_registrations");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Name).HasMaxLength(120).IsRequired();
        builder.Property(r => r.Phone).HasMaxLength(20).IsRequired();
        builder.Property(r => r.Email).HasMaxLength(254);
        builder.HasIndex(r => new { r.TenantId, r.WorkshopId, r.CreatedAt });
        // Restrict: apagar oficina com inscritos é bloqueado no domínio E no banco.
        builder.HasOne<Workshop>().WithMany().HasForeignKey(r => r.WorkshopId).OnDelete(DeleteBehavior.Restrict);
    }
}
