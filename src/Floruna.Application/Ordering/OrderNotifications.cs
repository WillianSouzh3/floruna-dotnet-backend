using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Application.Outbox;
using Floruna.Domain.Ordering;

namespace Floruna.Application.Ordering;

/// <summary>
/// E-mails de pedido, entregues pela OUTBOX: se o SMTP falhar, a outbox tenta de novo
/// com backoff (e não reenvia o que já foi entregue: recibo por handler). Antes, um
/// e-mail perdido no meio do caminho simplesmente sumia.
/// </summary>
internal sealed class OrderEmailHandlers(IOrderRepository orders, IEmailDelivery email, ITenantContext tenantContext)
    : INotificationHandler<DomainEventNotification<OrderPlaced>>,
      INotificationHandler<DomainEventNotification<OrderStatusChanged>>
{
    public async Task Handle(DomainEventNotification<OrderPlaced> notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var order = await orders.GetAsync(notification.Event.OrderId, cancellationToken);
        if (order?.Customer.Email is not { } to || !email.IsEnabled)
        {
            return;
        }

        await email.DeliverAsync(new EmailMessage(to, "Recebemos seu pedido",
            $"Olá, {order.Customer.Name}!\n\nRecebemos seu pedido na loja {tenantContext.Tenant?.Slug}. " +
            $"Total: R$ {order.TotalCents / 100m:0.00}.\nAssim que o pagamento for confirmado, avisamos por aqui."), cancellationToken);
    }

    public async Task Handle(DomainEventNotification<OrderStatusChanged> notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var order = await orders.GetAsync(notification.Event.OrderId, cancellationToken);
        if (order?.Customer.Email is not { } to || !email.IsEnabled)
        {
            return;
        }

        await email.DeliverAsync(new EmailMessage(to, $"Pedido {OrderStatusContract.Label(notification.Event.To).ToLowerInvariant()}",
            $"Olá, {order.Customer.Name}!\n\nSeu pedido agora está: {OrderStatusContract.Label(notification.Event.To)}."), cancellationToken);
    }
}
