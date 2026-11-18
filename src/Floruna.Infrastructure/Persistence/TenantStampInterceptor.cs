using Floruna.Application.Abstractions;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Floruna.Infrastructure.Persistence;

/// <summary>
/// Carimba o TenantId em toda inserção, a partir do CONTEXTO da requisição.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Sobrescreve qualquer valor que veio no objeto: "o payload nunca escolhe a loja" (RN-01).</item>
/// <item>Sem loja no contexto, lança: nada é gravado (fail-closed).</item>
/// <item>Proíbe mover uma entidade de uma loja para outra num UPDATE.</item>
/// </list>
/// Interceptor (e não código no SaveChanges) porque é uma preocupação
/// transversal plugável: o DbContext não precisa saber que ela existe.
/// </remarks>
internal sealed class TenantStampInterceptor(ITenantContext tenantContext) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<ITenantOwned>())
        {
            var tenantProperty = entry.Property(e => e.TenantId);
            switch (entry.State)
            {
                case EntityState.Added:
                    tenantProperty.CurrentValue = tenantContext.RequiredTenantId;
                    break;
                case EntityState.Modified when tenantProperty.IsModified:
                    throw new InvalidOperationException("Uma entidade não pode trocar de loja.");
                case EntityState.Modified or EntityState.Deleted when tenantProperty.OriginalValue != tenantContext.RequiredTenantId:
                    throw new InvalidOperationException("Alteração de dado de outra loja bloqueada.");
                default:
                    break;
            }
        }
    }
}
