using Floruna.SharedKernel;

namespace Floruna.Domain.Platform;

/// <summary>
/// Repository do agregado Tenant. A interface mora no DOMÍNIO (ela fala a
/// língua do negócio); a implementação com EF Core mora na Infrastructure.
/// Isso é Inversão de Dependência: o domínio define o contrato, o detalhe obedece.
/// </summary>
public interface ITenantRepository
{
    void Add(Tenant tenant);

    Task<Tenant?> GetByIdAsync(TenantId id, CancellationToken cancellationToken);
}
