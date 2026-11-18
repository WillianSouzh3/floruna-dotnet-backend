using Floruna.Domain.Customers;
using Floruna.Domain.Identity;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Floruna.Infrastructure.Persistence;

internal sealed class TenantIdConverter() : ValueConverter<TenantId, Guid>(id => id.Value, value => new TenantId(value));

internal sealed class StaffUserIdConverter() : ValueConverter<StaffUserId, Guid>(id => id.Value, value => new StaffUserId(value));

internal sealed class CustomerIdConverter() : ValueConverter<CustomerId, Guid>(id => id.Value, value => new CustomerId(value));

internal sealed class EmailConverter() : ValueConverter<Email, string>(email => email.Value, value => Email.FromPersistence(value));
