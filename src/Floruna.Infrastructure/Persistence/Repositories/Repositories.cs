using Floruna.Domain.Customers;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Persistence.Repositories;

// Repositórios finos: o filtro de loja e a transação já vêm do DbContext.
// O valor do padrão aqui é o CONTRATO estreito (só o que o agregado precisa),
// não "esconder o EF" por esconder.

internal sealed class TenantRepository(FlorunaDbContext db) : ITenantRepository
{
    public void Add(Tenant tenant) => db.Tenants.Add(tenant);

    public Task<Tenant?> GetByIdAsync(TenantId id, CancellationToken cancellationToken) =>
        db.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<bool> IsDomainVerifiedByAnotherAsync(string domain, TenantId except, CancellationToken cancellationToken) =>
        db.Tenants.AnyAsync(t => t.CustomDomain == domain && t.CustomDomainVerified && t.Id != except, cancellationToken);
}

internal sealed class StaffUserRepository(FlorunaDbContext db) : IStaffUserRepository
{
    public void Add(StaffUser user) => db.StaffUsers.Add(user);

    public Task<StaffUser?> GetByIdAsync(StaffUserId id, CancellationToken cancellationToken) =>
        db.StaffUsers.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<StaffUser?> FindByEmailAsync(Email email, CancellationToken cancellationToken) =>
        db.StaffUsers.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) => db.StaffUsers.CountAsync(cancellationToken);

    public Task<int> CountByRoleAsync(string role, CancellationToken cancellationToken) =>
        db.StaffUsers.CountAsync(u => u.Role == role, cancellationToken);

    public async Task<IReadOnlyList<StaffUser>> ListAsync(CancellationToken cancellationToken) =>
        await db.StaffUsers.OrderBy(u => u.CreatedAt).ToListAsync(cancellationToken);

    public void Remove(StaffUser user) => db.StaffUsers.Remove(user);

    // IgnoreQueryFilters: escape CONSCIENTE do filtro da loja, sempre com a loja
    // explícita na consulta. Só estes dois métodos fazem isso (ver ADR 0002).
    public Task<StaffUser?> GetInTenantAsync(StaffUserId id, TenantId tenantId, CancellationToken cancellationToken) =>
        db.StaffUsers.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id && u.TenantId == tenantId, cancellationToken);

    public Task<StaffUser?> FindMasterOfTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        db.StaffUsers.IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && u.Role == Roles.Master)
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
}

internal sealed class CustomerRepository(FlorunaDbContext db) : ICustomerRepository
{
    public void Add(Customer customer) => db.Customers.Add(customer);

    public Task<Customer?> FindByEmailAsync(Email email, CancellationToken cancellationToken) =>
        db.Customers.FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
}
