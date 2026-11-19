using Floruna.Domain.Platform;
using Floruna.Domain.Pos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Floruna.Infrastructure.Persistence.Configurations;

internal sealed class CashSessionConfiguration : IEntityTypeConfiguration<CashSession>
{
    public void Configure(EntityTypeBuilder<CashSession> builder)
    {
        builder.ToTable("cash_sessions", t => t.HasCheckConstraint("ck_cash_sessions_float_not_negative", "opening_float_cents >= 0"));
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.OperatorName).HasMaxLength(120).IsRequired();
        builder.OwnsMany(s => s.Movements, m => m.ToJson("movements"));
        builder.Navigation(s => s.Movements).UsePropertyAccessMode(PropertyAccessMode.Field);
        // No máximo UM caixa aberto por operador: fecha a corrida de dois "abrir caixa" no mesmo login.
        builder.HasIndex(s => new { s.TenantId, s.OperatorId }).IsUnique().HasFilter("status = 'Open'").HasDatabaseName("ux_cash_sessions_one_open_per_operator");
        builder.HasIndex(s => new { s.TenantId, s.Status, s.ClosedAt });
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
}
