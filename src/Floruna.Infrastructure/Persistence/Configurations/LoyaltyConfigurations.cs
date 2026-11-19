using Floruna.Domain.Loyalty;
using Floruna.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class LoyaltyProgramConfiguration : IEntityTypeConfiguration<LoyaltyProgram>
{
    public void Configure(EntityTypeBuilder<LoyaltyProgram> builder)
    {
        builder.ToTable("loyalty_programs", t =>
            t.HasCheckConstraint("ck_loyalty_programs_cashback_share", $"max_cashback_percent_of_order BETWEEN 0 AND {LoyaltyProgram.MaxCashbackShareAllowed}"));
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.CashbackPercent).HasPrecision(5, 2);
        builder.Ignore(p => p.Rules);
        builder.OwnsMany(p => p.Tiers, t => t.ToJson("tiers")); // configuração lida inteira: documento
        builder.HasIndex(p => p.TenantId).IsUnique(); // um programa por loja
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LoyaltyAccountConfiguration : IEntityTypeConfiguration<LoyaltyAccount>
{
    public void Configure(EntityTypeBuilder<LoyaltyAccount> builder)
    {
        builder.ToTable("loyalty_accounts", t => t.HasCheckConstraint("ck_loyalty_accounts_balance", "cashback_balance >= 0"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Email).HasMaxLength(254).IsRequired();
        builder.Property(a => a.TierKey).HasMaxLength(40).IsRequired();
        builder.Property(a => a.FlowerKinds).IsRequired(); // text[]
        builder.Ignore(a => a.NewEntries);
        builder.HasMany(a => a.ActiveCredits).WithOne().HasForeignKey(e => e.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(a => a.ActiveCredits).HasField("_activeCredits").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(a => new { a.TenantId, a.Email }).IsUnique(); // a carteira é o e-mail NA LOJA
        builder.HasIndex(a => new { a.TenantId, a.TotalSpentCents }); // ranking do painel
        builder.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class LoyaltyEntryConfiguration : IEntityTypeConfiguration<LoyaltyEntry>
{
    public void Configure(EntityTypeBuilder<LoyaltyEntry> builder)
    {
        builder.ToTable("loyalty_entries", t =>
        {
            t.HasCheckConstraint("ck_loyalty_entries_remaining", "remaining >= 0 AND remaining <= GREATEST(amount, 0)");
            t.HasCheckConstraint("ck_loyalty_entries_amount", "amount <> 0");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.DedupeKey).HasMaxLength(160).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(200).IsRequired();
        builder.HasIndex(e => new { e.TenantId, e.DedupeKey }).IsUnique(); // retry nunca credita duas vezes
        builder.HasIndex(e => new { e.AccountId, e.CreatedAt }); // extrato
        // Lotes com saldo, por validade: resgate FIFO-por-vencimento e job de expiração.
        builder.HasIndex(e => new { e.AccountId, e.ExpiresAt }).HasFilter("remaining > 0").HasDatabaseName("ix_loyalty_entries_open_lots");
        builder.HasIndex(e => e.ExpiresAt).HasFilter("remaining > 0").HasDatabaseName("ix_loyalty_entries_expiring");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
