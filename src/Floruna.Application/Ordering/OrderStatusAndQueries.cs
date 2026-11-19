using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Domain.Delivery;
using Floruna.Domain.Inventory;
using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.Domain.Promotions;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Ordering;

/// <summary><c>PATCH /orders/:id/status</c> (e o job de abandonados, como ator <c>system</c>).</summary>
public sealed record ChangeOrderStatusCommand(
    Guid OrderId, string Status, string? ExpectedState, string? IdempotencyKey, string? Reason, Guid? ActorId, string ActorRole)
    : ICommand<OrderEnvelope>;

internal sealed class ChangeOrderStatusValidator : AbstractValidator<ChangeOrderStatusCommand>
{
    public ChangeOrderStatusValidator()
    {
        RuleFor(x => x.Status).Must(s => OrderStatusContract.Parse(s) is not null).WithMessage("Status inválido.");
        RuleFor(x => x.ExpectedState).Must(s => OrderStatusContract.Parse(s) is not null).When(x => x.ExpectedState is not null).WithMessage("Estado esperado inválido.");
        RuleFor(x => x.IdempotencyKey).NotEmpty().When(x => x.ExpectedState is not null).WithMessage("idempotencyKey é obrigatória com expectedState.");
        RuleFor(x => x.ExpectedState).NotEmpty().When(x => x.IdempotencyKey is not null).WithMessage("expectedState é obrigatório com idempotencyKey.");
        RuleFor(x => x.Reason).MaximumLength(300);
    }
}

/// <summary>
/// Efeitos de cada transição ficam AQUI, na mesma transação da mudança de estado
/// (consistência forte onde dói, RNF-04):
/// <list type="bullet">
/// <item><b>confirmado</b>: cada reserva vira VENDA no ledger (custo dos lotes, FEFO).</item>
/// <item><b>cancelado</b> ainda pendente: solta reservas, a vaga de entrega e o uso do cupom.</item>
/// <item><b>cancelado</b> depois de confirmado: a flor já saiu do estoque e NÃO volta sozinha
/// (perecível devolvido exige inspeção; regra floral 7).</item>
/// </list>
/// E-mails e demais efeitos eventuais saem pela outbox (evento OrderStatusChanged).
/// </summary>
internal sealed class ChangeOrderStatusHandler(
    IOrderRepository orders,
    IInventoryRepository inventory,
    ICouponRepository coupons,
    IDeliveryRepository delivery,
    IUnitOfWork unitOfWork,
    ICatalogCache cache,
    Payments.PaidOrderCancellation paidCancellation,
    IPaymentRepository payments,
    TimeProvider clock) : ICommandHandler<ChangeOrderStatusCommand, OrderEnvelope>
{
    public async Task<Result<OrderEnvelope>> Handle(ChangeOrderStatusCommand request, CancellationToken cancellationToken)
    {
        var target = OrderStatusContract.Parse(request.Status)!.Value;
        var expected = OrderStatusContract.Parse(request.ExpectedState);

        // Pedido PAGO: o dinheiro volta ANTES do cancelamento (saga com chamada externa, fora da transação).
        var refundedNow = false;
        if (target == OrderStatus.Cancelado)
        {
            var paidBefore = await payments.FindByOrderAsync(new OrderId(request.OrderId), cancellationToken) is { Status: PaymentIntentStatus.Paid };
            var refund = await paidCancellation.EnsureRefundedAsync(new OrderId(request.OrderId), expected, request.Reason,
                new Domain.Audit.AuditActor(request.ActorId, request.ActorRole, null, string.Empty), cancellationToken);
            if (refund.IsFailure)
            {
                return refund.Error;
            }

            refundedNow = paidBefore;
        }

        var result = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var order = await orders.GetAsync(new OrderId(request.OrderId), ct);
            if (order is null)
            {
                return Result.Failure<Order>(OrderErrors.NotFound);
            }

            var previous = order.Status;
            var now = clock.GetUtcNow();
            var change = order.ChangeStatus(target, request.ActorId, request.ActorRole, request.Reason, expected, request.IdempotencyKey, now);
            if (change.IsFailure)
            {
                return Result.Failure<Order>(change.Error);
            }

            if (change.Value is not null)
            {
                var effects = previous == OrderStatus.Pendente && target != OrderStatus.Cancelado
                    ? await CommitSaleAsync(order, now, ct)
                    : previous == OrderStatus.Pendente && target == OrderStatus.Cancelado
                        ? await ReleaseAsync(order, now, ct)
                        : Result.Success();
                if (effects.IsFailure)
                {
                    return Result.Failure<Order>(effects.Error);
                }
            }

            await unitOfWork.SaveChangesAsync(ct);
            return Result.Success(order);
        }, cancellationToken);

        if (result.IsFailure)
        {
            if (refundedNow && await payments.FindByOrderAsync(new OrderId(request.OrderId), cancellationToken) is { } intent)
            {
                // O dinheiro já voltou, mas o cancelamento falhou: nunca some em silêncio.
                intent.FlagReconciliationIssue("refund_completed_order_not_cancelled");
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return result.Error;
        }

        await cache.InvalidateAsync(cancellationToken);
        return new OrderEnvelope(OrderResponse.From(result.Value, internalView: true));
    }

    /// <summary>Reserva → venda, item a item, em ordem crescente de produto (mesma ordem de trava do checkout).</summary>
    private async Task<Result> CommitSaleAsync(Order order, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (item, index) in order.Items.Select((item, index) => (item, index)).OrderBy(x => x.item.ProductId.Value))
        {
            var saleKey = ReservationKeys.Sale(order.Id, index);
            if (await inventory.MovementExistsAsync(saleKey, ct))
            {
                continue; // já baixado (retry): não baixa duas vezes
            }

            var reservation = await inventory.GetReservationByKeyForUpdateAsync(ReservationKeys.For(order.Id, index), ct);
            var stock = await inventory.GetForUpdateAsync(item.ProductId, ct);
            if (stock is null)
            {
                return OrderErrors.InsufficientStock(item.Name);
            }

            // Reserva ainda segurada: consome ela. Vencida/solta (confirmação tardia): vende do livre, se houver.
            var fromReservation = reservation is { State: ReservationState.Held } held && held.Commit(now) ? item.Quantity : 0;
            var sale = stock.Sell(item.Quantity, now, fromReservation);
            if (sale.IsFailure)
            {
                return OrderErrors.InsufficientStock(item.Name);
            }

            inventory.Add(StockMovement.Record(order.TenantId, item.ProductId, item.Name, sale.Value,
                $"Pedido {order.Id}", "pedido", now, order.Id.Value, saleKey));
        }

        return Result.Success();
    }

    private async Task<Result> ReleaseAsync(Order order, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (item, index) in order.Items.Select((item, index) => (item, index)).OrderBy(x => x.item.ProductId.Value))
        {
            var reservation = await inventory.GetReservationByKeyForUpdateAsync(ReservationKeys.For(order.Id, index), ct);
            var stock = await inventory.GetForUpdateAsync(item.ProductId, ct);
            if (reservation is not null && reservation.Release("pedido cancelado", now))
            {
                stock?.ReleaseHold(reservation.Quantity);
            }
        }

        if (order is { DeliveryDate: { } date, DeliveryWindow: { } window })
        {
            await delivery.FreeAsync(date, window, order.DeliverySlotScope, ct);
        }

        if (order.CouponId is { } couponId)
        {
            await coupons.ReleaseUseAsync(couponId, order.Id.Value, ct);
        }

        return Result.Success();
    }
}

/// <summary>
/// Leitura de um pedido (<c>GET /orders/:id</c>): por token (página de sucesso pública),
/// pelo dono (cliente logado) ou pela equipe.
/// </summary>
public sealed record GetOrderQuery(Guid OrderId, string? Token, Guid? UserId, string? Role) : IQuery<OrderEnvelope>;

public sealed record ListOrdersQuery(OrderListFilter Filter) : IQuery<OrderPage>;

public sealed record ListMyOrdersQuery(Guid CustomerId, int Page, int Limit) : IQuery<OrderPage>;

internal sealed class OrderQueryHandlers(IOrderRepository orders, IOrderQueries queries)
    : IQueryHandler<GetOrderQuery, OrderEnvelope>,
      IQueryHandler<ListOrdersQuery, OrderPage>,
      IQueryHandler<ListMyOrdersQuery, OrderPage>
{
    public async Task<Result<OrderEnvelope>> Handle(GetOrderQuery request, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(new OrderId(request.OrderId), cancellationToken);

        if (request.Token is not null)
        {
            // Mesmo erro para "não existe" e "token errado": não revela quais IDs existem (RN-47).
            return order is not null && order.CanBeReadWith(request.Token)
                ? new OrderEnvelope(OrderResponse.From(order, internalView: false))
                : OrderErrors.NotFound;
        }

        if (request.Role is not null && Domain.Identity.Roles.Staff.Contains(request.Role))
        {
            return order is null ? OrderErrors.NotFound : new OrderEnvelope(OrderResponse.From(order, internalView: true));
        }

        if (request.Role == Domain.Identity.Roles.Customer)
        {
            return order is not null && order.CustomerId == request.UserId
                ? new OrderEnvelope(OrderResponse.From(order, internalView: false))
                : OrderErrors.NotFound;
        }

        return OrderErrors.ReadRequiresToken;
    }

    public async Task<Result<OrderPage>> Handle(ListOrdersQuery request, CancellationToken cancellationToken) =>
        await queries.ListAsync(request.Filter, null, cancellationToken);

    public async Task<Result<OrderPage>> Handle(ListMyOrdersQuery request, CancellationToken cancellationToken) =>
        await queries.ListAsync(new OrderListFilter(null, null, null, null, request.Page, request.Limit), request.CustomerId, cancellationToken);
}
