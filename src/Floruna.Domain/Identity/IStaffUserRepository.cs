using Floruna.SharedKernel;

namespace Floruna.Domain.Identity;

/// <summary>Consultas aqui já são filtradas pelo tenant atual (pela infraestrutura).</summary>
public interface IStaffUserRepository
{
    void Add(StaffUser user);

    Task<StaffUser?> GetByIdAsync(StaffUserId id, CancellationToken cancellationToken);

    Task<StaffUser?> FindByEmailAsync(Email email, CancellationToken cancellationToken);

    Task<int> CountAsync(CancellationToken cancellationToken);

    Task<int> CountByRoleAsync(string role, CancellationToken cancellationToken);

    Task<IReadOnlyList<StaffUser>> ListAsync(CancellationToken cancellationToken);

    void Remove(StaffUser user);

    /// <summary>
    /// Busca FORA do filtro da loja atual, na loja informada. Uso restrito:
    /// validar a sessão do superadmin, que navega por todas as lojas.
    /// </summary>
    Task<StaffUser?> GetInTenantAsync(StaffUserId id, TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Primeiro master de uma loja (fora do filtro). Uso: impersonação pelo suporte.</summary>
    Task<StaffUser?> FindMasterOfTenantAsync(TenantId tenantId, CancellationToken cancellationToken);
}
