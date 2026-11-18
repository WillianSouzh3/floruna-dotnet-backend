using Floruna.Domain.Audit;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class SingleUseTokenConfiguration : IEntityTypeConfiguration<SingleUseToken>
{
    public void Configure(EntityTypeBuilder<SingleUseToken> builder)
    {
        builder.ToTable("single_use_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.SecretHash).HasMaxLength(64).IsRequired();

        // Consulta do consumo: (loja, propósito, hash). Único: o mesmo segredo não existe duas vezes.
        builder.HasIndex(t => new { t.TenantId, t.Purpose, t.SecretHash }).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.UserId, t.Purpose });
        builder.HasOne<StaffUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Action).HasMaxLength(64).IsRequired();
        builder.Property(a => a.ActorRole).HasMaxLength(20).IsRequired();
        builder.Property(a => a.Ip).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Entity).HasMaxLength(32).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Summary).HasMaxLength(500).IsRequired();
        builder.HasIndex(a => new { a.TenantId, a.CreatedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LoginAttemptBucketConfiguration : IEntityTypeConfiguration<LoginAttemptBucket>
{
    public void Configure(EntityTypeBuilder<LoginAttemptBucket> builder)
    {
        builder.ToTable("login_attempts");
        builder.HasKey(b => b.Key);
        builder.Property(b => b.Key).HasMaxLength(64);
        builder.Property(b => b.GroupKey).HasMaxLength(64).IsRequired();
        builder.HasIndex(b => b.GroupKey);
    }
}
