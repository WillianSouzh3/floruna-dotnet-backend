using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Payments;

internal sealed class PaymentRepository(FlorunaDbContext db) : IPaymentRepository
{
    public void Add(PaymentIntent intent) => db.PaymentIntents.Add(intent);

    public void Add(PaymentNotification notification) => db.PaymentNotifications.Add(notification);

    public void Add(PaymentSettings settings) => db.PaymentSettings.Add(settings);

    public Task<PaymentIntent?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.PaymentIntents.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public Task<PaymentIntent?> FindByOrderAsync(OrderId orderId, CancellationToken cancellationToken) =>
        db.PaymentIntents.FirstOrDefaultAsync(i => i.OrderId == orderId, cancellationToken);

    public Task<PaymentIntent?> FindByProviderPaymentIdAsync(string providerPaymentId, CancellationToken cancellationToken) =>
        db.PaymentIntents.FirstOrDefaultAsync(i => i.ProviderPaymentId == providerPaymentId, cancellationToken);

    // O filtro de loja já restringe a linha à loja atual (PK = tenant_id).
    public Task<PaymentSettings?> GetSettingsAsync(CancellationToken cancellationToken) =>
        db.PaymentSettings.FirstOrDefaultAsync(cancellationToken);
}
