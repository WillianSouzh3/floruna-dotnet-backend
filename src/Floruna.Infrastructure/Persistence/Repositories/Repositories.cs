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
}

internal sealed class StaffUserRepository(FlorunaDbContext db) : IStaffUserRepository
{
    public void Add(StaffUser user) => db.StaffUsers.Add(user);

    public Task<StaffUser?> GetByIdAsync(StaffUserId id, CancellationToken cancellationToken) =>
        db.StaffUsers.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<StaffUser?> FindByEmailAsync(Email email, CancellationToken cancellationToken) =>
        db.StaffUsers.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) => db.StaffUsers.CountAsync(cancellationToken);
}

internal sealed class CustomerRepository(FlorunaDbContext db) : ICustomerRepository
{
    public void Add(Customer customer) => db.Customers.Add(customer);

    public Task<Customer?> FindByEmailAsync(Email email, CancellationToken cancellationToken) =>
        db.Customers.FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
}
