using Floruna.SharedKernel;

namespace Floruna.Domain.Customers;

public interface ICustomerRepository
{
    void Add(Customer customer);

    Task<Customer?> FindByEmailAsync(Email email, CancellationToken cancellationToken);
}
