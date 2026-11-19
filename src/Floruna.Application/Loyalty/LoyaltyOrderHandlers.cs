using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Application.Outbox;
using Floruna.Domain.Catalog;
using Floruna.Domain.Loyalty;
using Floruna.Domain.Ordering;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;

namespace Floruna.Application.Loyalty;

/// <summary>
/// Reage às mudanças de status do pedido pela OUTBOX (consistência eventual, mas garantida):
/// <list type="bullet">
/// <item><b>entregue</b>: credita o cashback e atualiza histórico e nível.</item>
/// <item><b>cancelado</b>: devolve o cashback que o pedido usou.</item>
/// </list>
/// Acumular e estornar são dois lados do mesmo fato; ficam juntos para um não ser ligado sem o outro.
/// Idempotência em duas camadas: marcador no pedido (gravado na MESMA transação do crédito)
/// e índice único da <c>dedupeKey</c> no razão.
/// </summary>
internal sealed class LoyaltyOrderHandlers(
    IOrderRepository orders,
    ILoyaltyRepository loyalty,
    ICatalogRepository catalog,
    ITenantContext tenantContext,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : INotificationHandler<DomainEventNotification<OrderStatusChanged>>
{
    public async Task Handle(DomainEventNotification<OrderStatusChanged> notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var task = notification.Event.To switch
        {
            OrderStatus.Entregue => AccrueAsync(notification.Event.OrderId, cancellationToken),
            OrderStatus.Cancelado => ReverseAsync(notification.Event.OrderId, cancellationToken),
            _ => Task.CompletedTask,
        };
        await task;
    }

    private async Task AccrueAsync(OrderId orderId, CancellationToken cancellationToken)
    {
        if (tenantContext.Tenant is not { } tenant || !PlanCatalog.For(tenant.EffectivePlan).Allows(PlanFeature.Loyalty))
        {
            return;
        }

        var program = await loyalty.GetProgramAsync(cancellationToken);
        if (program is not { Enabled: true })
        {
            return;
        }

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var order = await orders.GetAsync(orderId, ct);
            var now = clock.GetUtcNow();
            if (order is not { Status: OrderStatus.Entregue, Customer.Email: { } email } || !order.MarkLoyaltyAccrued(now))
            {
                return Result.Success();
            }

            var account = await loyalty.GetForUpdateAsync(email, ct);
            if (account is null)
            {
                account = LoyaltyAccount.Open(order.TenantId, email, now);
                loyalty.Add(account);
            }

            var earned = DiscountPolicy.CashbackEarned(order.SubtotalCents, order.DiscountCents, order.CashbackUsedCents, program.CashbackPercent);
            account.Earn(earned, now.AddDays(program.CashbackExpiryDays), LoyaltyKeys.Earn(order.Id.Value), $"Cashback do pedido {order.Id.ToString()[..8]}", now);
            account.RecordPurchase(order.SubtotalCents - order.DiscountCents, await CategoriesAsync(order, ct), program.Tiers);
            loyalty.Track(account);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }

    private async Task ReverseAsync(OrderId orderId, CancellationToken cancellationToken)
    {
        // Devolve mesmo com o programa desligado depois: o saldo era do cliente.
        var program = await loyalty.GetProgramAsync(cancellationToken);
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var order = await orders.GetAsync(orderId, ct);
            var now = clock.GetUtcNow();
            if (order is not { CashbackUsedCents: > 0, LoyaltyAccountId: { } accountId } || !order.MarkLoyaltyReversed(now))
            {
                return Result.Success();
            }

            var account = await loyalty.GetByIdForUpdateAsync(accountId, ct);
            if (account is null)
            {
                return Result.Success();
            }

            account.ReverseRedemption(order.CashbackUsedCents, now.AddDays(program?.CashbackExpiryDays ?? 365), LoyaltyKeys.Reverse(order.Id.Value), now);
            loyalty.Track(account);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }

    private async Task<List<string>> CategoriesAsync(Order order, CancellationToken ct)
    {
        var categories = new List<string>();
        foreach (var productId in order.Items.Select(i => i.ProductId).Distinct())
        {
            if (await catalog.GetProductAsync(productId, ct) is { } product)
            {
                categories.Add(product.Category.ToString().ToLowerInvariant());
            }
        }

        return categories;
    }
}
