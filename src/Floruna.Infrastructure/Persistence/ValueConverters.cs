using Floruna.Domain.Catalog;
using Floruna.Domain.Customers;
using Floruna.Domain.Identity;
using Floruna.Domain.Ordering;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Floruna.Infrastructure.Persistence;

internal sealed class TenantIdConverter() : ValueConverter<TenantId, Guid>(id => id.Value, value => new TenantId(value));

internal sealed class StaffUserIdConverter() : ValueConverter<StaffUserId, Guid>(id => id.Value, value => new StaffUserId(value));

internal sealed class CustomerIdConverter() : ValueConverter<CustomerId, Guid>(id => id.Value, value => new CustomerId(value));

internal sealed class EmailConverter() : ValueConverter<Email, string>(email => email.Value, value => Email.FromPersistence(value));

internal sealed class ProductIdConverter() : ValueConverter<ProductId, Guid>(id => id.Value, value => new ProductId(value));

internal sealed class CategoryIdConverter() : ValueConverter<CategoryId, Guid>(id => id.Value, value => new CategoryId(value));

internal sealed class CollectionIdConverter() : ValueConverter<CollectionId, Guid>(id => id.Value, value => new CollectionId(value));

internal sealed class VariantIdConverter() : ValueConverter<VariantId, string>(id => id.Value, value => new VariantId(value));

internal sealed class OrderIdConverter() : ValueConverter<OrderId, Guid>(id => id.Value, value => new OrderId(value));
