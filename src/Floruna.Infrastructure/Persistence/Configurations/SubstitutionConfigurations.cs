using Floruna.Domain.Platform;
using Floruna.Domain.Substitutions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class SubstitutionPolicyConfiguration : IEntityTypeConfiguration<SubstitutionPolicy>
{
    public void Configure(EntityTypeBuilder<SubstitutionPolicy> builder)
    {
        builder.ToTable("substitution_policies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasIndex(p => p.TenantId).IsUnique();
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubstitutionProposalConfiguration : IEntityTypeConfiguration<SubstitutionProposal>
{
    public void Configure(EntityTypeBuilder<SubstitutionProposal> builder)
    {
        builder.ToTable("substitution_proposals", t => t.HasCheckConstraint("ck_substitution_proposals_quantity", "quantity > 0"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.OriginalName).HasMaxLength(200).IsRequired();
        builder.Property(p => p.SubstituteName).HasMaxLength(200).IsRequired();
        builder.Property(p => p.ValueDeltaPercent).HasPrecision(7, 2);
        builder.Property(p => p.ConsentTokenHash).HasMaxLength(64).IsRequired();
        builder.Property(p => p.DecidedBy).HasMaxLength(20).IsRequired();
        builder.Property(p => p.Reason).HasMaxLength(300).IsRequired();
        builder.Property(p => p.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.IdempotencyKey }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.OrderId, p.CreatedAt });
        builder.HasIndex(p => p.ExpiresAt).HasFilter("state = 'Proposed'").HasDatabaseName("ix_substitution_proposals_pending_expiry");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
