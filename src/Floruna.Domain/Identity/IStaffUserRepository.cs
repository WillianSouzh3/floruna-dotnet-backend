using Floruna.SharedKernel;

namespace Floruna.Domain.Identity;

/// <summary>Consultas aqui já são filtradas pelo tenant atual (pela infraestrutura).</summary>
public interface IStaffUserRepository
{
    void Add(StaffUser user);

    Task<StaffUser?> GetByIdAsync(StaffUserId id, CancellationToken cancellationToken);

    Task<StaffUser?> FindByEmailAsync(Email email, CancellationToken cancellationToken);

    Task<int> CountAsync(CancellationToken cancellationToken);
}
