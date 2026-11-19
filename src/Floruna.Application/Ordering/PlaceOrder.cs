using System.Text.RegularExpressions;
using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Delivery;
using Floruna.Application.Messaging;
using Floruna.Domain.Catalog;
using Floruna.Domain.Delivery;
using Floruna.Domain.Inventory;
using Floruna.Domain.Loyalty;
using Floruna.Domain.Ordering;
using Floruna.Domain.Promotions;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Ordering;

/// <summary>Checkout da vitrine (<c>POST /orders</c>). Login opcional: <paramref name="CustomerId"/> vem do token, se houver.</summary>
/// <param name="SessionEmail">E-mail DO TOKEN do cliente: a única carteira de onde o cashback pode sair.</param>
public sealed record PlaceOrderCommand(CheckoutInput Input, Guid? CustomerId, string? SessionEmail = null) : ICommand<OrderEnvelope>;

internal sealed partial class PlaceOrderValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderValidator()
    {
        RuleFor(x => x.Input.CustomerName).NotEmpty().Length(2, 120).WithMessage("Informe o nome (2 a 120 caracteres).");
        RuleFor(x => x.Input.CustomerPhone).Matches(Phone()).WithMessage("Telefone inválido. Use (11) 99999-9999.");
        RuleFor(x => x.Input.CustomerEmail).EmailAddress().When(x => !string.IsNullOrEmpty(x.Input.CustomerEmail)).WithMessage("Email inválido.");
        RuleFor(x => x.Input.Items).NotEmpty().WithMessage("O carrinho está vazio.");
        RuleFor(x => x.Input.Items!.Count).LessThanOrEqualTo(50).When(x => x.Input.Items is not null).WithMessage("O carrinho excede o limite de 50 itens.");
        RuleForEach(x => x.Input.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotEmpty().WithMessage("Produto inválido.");
            item.RuleFor(i => i.Quantity).InclusiveBetween(1, 100).WithMessage("Quantidade deve estar entre 1 e 100.");
        });
        RuleFor(x => x.Input.DeliveryMethod).Must(m => DeliveryMethodContract.Parse(m) is not null).WithMessage("Forma de entrega inválida.");
        RuleFor(x => x.Input.Address).NotNull().When(x => x.Input.DeliveryMethod != "retirada").WithMessage("Informe o endereço de entrega.");
        RuleFor(x => x.Input.Address!.ZipCode).Matches(@"^\d{5}-?\d{3}$").When(x => x.Input.Address is not null).WithMessage("CEP inválido. Use o formato 00000-000.");
        RuleFor(x => x.Input.GiftCard!.Message).NotEmpty().MaximumLength(300).When(x => x.Input.GiftCard is not null).WithMessage("A mensagem do cartão deve ter até 300 caracteres.");
        RuleFor(x => x.Input.IdempotencyKey).MaximumLength(120);
        RuleFor(x => x.Input.UseCashback).GreaterThanOrEqualTo(0).When(x => x.Input.UseCashback is not null).WithMessage("Cashback inválido.");
    }

    [GeneratedRegex(@"^\(?\d{2}\)?\s?9?\d{4}-?\d{4}$")]
    private static partial Regex Phone();
}

/// <summary>
/// O coração do negócio. Ordem dos passos (RN-40 a RN-44):
/// <list type="number">
/// <item>Idempotência: a mesma chave devolve o MESMO pedido.</item>
/// <item>Preços, frete e desconto calculados no servidor (o cliente não manda preço).</item>
/// <item>UMA transação: trava o estoque de cada produto (sempre na mesma ordem),
/// reserva, consome o cupom e a vaga de entrega, grava o pedido e os eventos (outbox).</item>
/// <item>Nada de chamada externa dentro da transação (pagamento é depois).</item>
/// </list>
/// </summary>
internal sealed class PlaceOrderHandler(
    ITenantContext tenantContext,
    IOrderRepository orders,
    OrderItemPricer pricer,
    IInventoryRepository inventory,
    ICouponRepository coupons,
    IDeliveryRepository delivery,
    DeliveryResolver deliveryResolver,
    IUnitOfWork unitOfWork,
    ICatalogCache cache,
    Payments.PaymentInitializer paymentInitializer,
    Loyalty.CheckoutLoyalty loyalty,
    ILoyaltyRepository loyaltyAccounts,
    Telemetry.FlorunaMetrics metrics,
    TimeProvider clock) : ICommandHandler<PlaceOrderCommand, OrderEnvelope>
{
    public async Task<Result<OrderEnvelope>> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        var input = request.Input;
        if (tenantContext.Tenant is not { } tenant)
        {
            return Domain.Platform.TenantErrors.NotResolved;
        }

        // 1. Idempotência (antes de qualquer trabalho).
        if (input.IdempotencyKey is { Length: > 0 } key && await orders.FindByIdempotencyKeyAsync(key, cancellationToken) is { } existing)
        {
            return new OrderEnvelope(OrderResponse.From(existing, internalView: false));
        }

        // 2. Itens com preço DO CATÁLOGO e snapshot (RN-40, RN-43).
        var items = await pricer.PriceAsync(input.Items!, cancellationToken);
        if (items.IsFailure)
        {
            return items.Error;
        }

        var subtotal = items.Value.Sum(i => i.LineTotalCents);
        var method = DeliveryMethodContract.Parse(input.DeliveryMethod)!.Value;
        var resolved = await deliveryResolver.ResolveAsync(method, input.Address?.ZipCode, input.DeliveryDate, input.DeliveryTimeWindow, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        // 3. Cupom (pré-checagem; o consumo real é condicional, dentro da transação).
        var now = clock.GetUtcNow();
        Coupon? coupon = null;
        var benefit = new CouponBenefit(0, false);
        if (!string.IsNullOrWhiteSpace(input.CouponCode))
        {
            coupon = await coupons.FindActiveByCodeAsync(input.CouponCode, cancellationToken);
            if (coupon is null)
            {
                return CouponErrors.Invalid;
            }

            var uses = coupon.PerCustomerLimit is null || string.IsNullOrWhiteSpace(input.CustomerEmail)
                ? 0
                : await coupons.CountRedemptionsAsync(coupon.Id, input.CustomerEmail, cancellationToken);
            var evaluated = coupon.Evaluate(subtotal, input.CustomerEmail, uses, now);
            if (evaluated.IsFailure)
            {
                return evaluated.Error;
            }

            benefit = evaluated.Value;
        }

        // 4. Fidelidade: desconto do nível, frete grátis e cashback, pela política da loja (RN-34, RN-35).
        var quote = await loyalty.QuoteAsync(input.CustomerEmail, request.SessionEmail, subtotal, resolved.Value.FeeCents,
            benefit.DiscountCents, benefit.FreeShipping, input.UseCashback ?? 0, cancellationToken);
        OrderTotals totals;
        if (quote is null)
        {
            totals = new OrderTotals(subtotal, benefit.DiscountCents, benefit.FreeShipping ? 0 : resolved.Value.FeeCents, 0);
        }
        else
        {
            var b = quote.Breakdown;
            if (quote.CouponDiscarded)
            {
                coupon = null; // o cliente não recebeu nada pelo cupom: não consome uso
            }

            totals = new OrderTotals(subtotal, b.MerchandiseDiscount, b.DeliveryFee, 0, b.CashbackUsed, b.TierDiscount);
        }

        var orderId = OrderId.New();
        var placed = Order.Place(orderId, new OrderPlacement(
            tenant.Id, input.IdempotencyKey, new CustomerContact(input.CustomerName!.Trim(), input.CustomerPhone!.Trim(), input.CustomerEmail?.Trim().ToLowerInvariant()),
            request.CustomerId, items.Value, totals, method, method == DeliveryMethod.Entrega ? input.Address : null,
            resolved.Value.Date, resolved.Value.Window, resolved.Value.ZoneId, resolved.Value.Slot?.ScopeZoneId, input.GiftCard, coupon?.Id, coupon?.Code,
            ParseSubstitution(input.SubstitutionMode)), now);
        if (placed.IsFailure)
        {
            return placed.Error;
        }

        var (order, accessToken) = placed.Value;

        Result<bool> written;
        try
        {
            written = await unitOfWork.ExecuteInTransactionAsync(ct => WriteAsync(order, coupon, quote, resolved.Value, now, ct), cancellationToken);
        }
        catch (UniqueConstraintViolationException) when (input.IdempotencyKey is { Length: > 0 } raced)
        {
            // Duas requisições com a mesma chave ao mesmo tempo: o índice único decidiu.
            // A que perdeu devolve o pedido da que venceu (RN-41).
            var winner = await orders.FindByIdempotencyKeyAsync(raced, cancellationToken);
            return winner is null ? OrderErrors.NotFound : new OrderEnvelope(OrderResponse.From(winner, internalView: false));
        }

        if (written.IsFailure)
        {
            return written.Error;
        }

        await cache.InvalidateAsync(cancellationToken); // disponibilidade da vitrine mudou
        metrics.OrderPlaced("online", order.TotalCents);

        // Cobrança DEPOIS do commit: a chamada ao gateway nunca segura a transação (RN-42).
        await paymentInitializer.InitializeAsync(order.Id, cancellationToken);
        return new OrderEnvelope(OrderResponse.From(order, internalView: false, accessToken));
    }

    private async Task<Result<bool>> WriteAsync(Order order, Coupon? coupon, Loyalty.LoyaltyQuote? quote, DeliveryResolution resolved, DateTimeOffset now, CancellationToken ct)
    {
        // Trava o estoque SEMPRE em ordem crescente de produto: dois checkouts com os
        // mesmos itens em ordem diferente nunca esperam um pelo outro em círculo (deadlock).
        var lines = order.Items.Select((item, index) => (item, index)).OrderBy(x => x.item.ProductId.Value).ToList();
        foreach (var (item, index) in lines)
        {
            var stock = await inventory.GetForUpdateAsync(item.ProductId, ct);
            if (stock is null || stock.Hold(item.Quantity, now).IsFailure)
            {
                return OrderErrors.InsufficientStock(item.Name);
            }

            inventory.Add(StockReservation.Hold(order.TenantId, item.ProductId, item.Quantity, ReservationOwnerType.Order,
                order.Id.ToString(), ReservationKeys.For(order.Id, index), now.Add(Order.PaymentWindow).AddMinutes(15)));
        }

        if (coupon is not null)
        {
            if (!await coupons.TryClaimUseAsync(coupon.Id, now, ct))
            {
                return CouponErrors.ExhaustedDuringCheckout;
            }

            coupons.Add(new CouponRedemption(order.TenantId, coupon.Id, coupon.Code, order.Customer.Email, order.Id.Value, order.CouponDiscountCents, order.TotalCents, now));
        }

        // Cashback sai NA MESMA transação: se o pedido não for gravado, o saldo não sai.
        // A conta é travada (FOR UPDATE): dois checkouts simultâneos não gastam o mesmo saldo.
        if (quote is { Breakdown.CashbackUsed: > 0, RedeemEmail: { } redeemEmail })
        {
            var wallet = await loyaltyAccounts.GetForUpdateAsync(redeemEmail, ct);
            if (wallet is null || wallet.Redeem(order.CashbackUsedCents, LoyaltyKeys.Redeem(order.Id.Value), "Usado no pedido", now).IsFailure)
            {
                return LoyaltyErrors.InsufficientBalance;
            }

            order.LinkLoyaltyAccount(wallet.Id);
            loyaltyAccounts.Track(wallet);
        }

        if (resolved is { Date: { } date, Window: { } window, Slot: { } slot } &&
            !await delivery.TryOccupyAsync(date, window, slot.ScopeZoneId, slot.Capacity, ct))
        {
            return DeliveryCalendar.SlotFull;
        }

        orders.Add(order); // o evento OrderPlaced vai para a outbox NESTE SaveChanges
        await unitOfWork.SaveChangesAsync(ct);
        return true;
    }

    private static SubstitutionMode ParseSubstitution(string? value) => value switch
    {
        "exact" => SubstitutionMode.Exact,
        "preapproved" => SubstitutionMode.Preapproved,
        _ => SubstitutionMode.AskMe, // padrão: perguntar antes (nunca substituir em silêncio)
    };
}

/// <summary>Chaves determinísticas: a reserva do item N do pedido X é sempre a mesma.</summary>
public static class ReservationKeys
{
    public static string For(OrderId orderId, int itemIndex) => $"order:{orderId}:{itemIndex}";

    public static string Sale(OrderId orderId, int itemIndex) => $"sale:{orderId}:{itemIndex}";
}

/// <summary>
/// Preço DO CATÁLOGO e snapshot de cada item (RN-40, RN-43). Usado pelo checkout online e pelo
/// PDV: o cliente (ou o operador) nunca informa preço.
/// </summary>
public sealed class OrderItemPricer(ICatalogRepository catalog)
{
    public async Task<Result<List<OrderItem>>> PriceAsync(IReadOnlyList<OrderItemInput> requested, CancellationToken cancellationToken)
    {
        var items = new List<OrderItem>(requested.Count);
        foreach (var line in requested)
        {
            if (!Guid.TryParse(line.ProductId, out var productGuid) ||
                await catalog.GetProductAsync(new ProductId(productGuid), cancellationToken) is not { IsActive: true } product)
            {
                return OrderErrors.ProductUnavailable(line.ProductId ?? string.Empty);
            }

            long unitPrice = product.EffectivePrice.Cents;
            string? variantId = null, variantLabel = null, sku = string.IsNullOrEmpty(product.Sku) ? null : product.Sku;
            if (product.Variants.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(line.VariantLabel))
                {
                    return OrderErrors.VariantRequired(product.Name);
                }

                var variant = product.Variants.FirstOrDefault(v => string.Equals(v.Label, line.VariantLabel.Trim(), StringComparison.OrdinalIgnoreCase));
                if (variant is not { IsActive: true })
                {
                    return OrderErrors.VariantUnavailable(product.Name, line.VariantLabel);
                }

                (unitPrice, variantId, variantLabel, sku) = (variant.PriceCents, variant.Id.Value, variant.Label, variant.Sku ?? sku);
            }

            items.Add(new OrderItem(product.Id, variantId, variantLabel, product.Name, sku, unitPrice, product.CostCents ?? 0, line.Quantity));
        }

        return items;
    }

}
