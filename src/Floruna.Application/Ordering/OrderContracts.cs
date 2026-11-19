using System.Text.Json.Serialization;
using Floruna.Application.Catalog;
using Floruna.Domain.Delivery;
using Floruna.Domain.Ordering;

namespace Floruna.Application.Ordering;

// Formato do pedido no contrato do Nest (subconjunto implementado até a Fase 4).

public sealed record OrderItemResponse(
    string ProductId, string Name, string? VariantLabel, string? VariantId, string? Sku, long UnitPrice,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? UnitCost, int Quantity);

public sealed record StatusHistoryResponse(string? From, string To, string At, string ActorRole, string Reason);

public sealed record OrderResponse(
    string Id,
    IReadOnlyList<OrderItemResponse> Items,
    long Subtotal,
    long Discount,
    long DeliveryFee,
    long GiftCardFee,
    long Total,
    string CustomerName,
    string CustomerPhone,
    string? CustomerEmail,
    string? CustomerId,
    string DeliveryMethod,
    DeliveryAddress? Address,
    string? DeliveryDate,
    string? DeliveryTimeWindow,
    GiftCard? GiftCard,
    string? CouponCode,
    string SubstitutionMode,
    string Status,
    IReadOnlyList<StatusHistoryResponse> StatusHistory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AccessToken,
    string PaymentStatus,
    string PaymentInitializationStatus,
    string? PaymentRail,
    string PixCode,
    string CreatedAt,
    string UpdatedAt,
    long TierDiscount,
    long CashbackUsed,
    string RefundStatus,
    long RefundedAmount)
{
    public string PaymentMethod { get; init; } = "pix";

    public string Origin { get; init; } = "online";

    /// <summary>Formas de pagamento do balcão (vazio no online).</summary>
    public IReadOnlyList<PosPaymentResponse> Payments { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CashSessionId { get; init; }

    /// <summary>Trocas aplicadas (os itens continuam como foram vendidos).</summary>
    public IReadOnlyList<OrderSubstitution> Substitutions { get; init; } = [];

    public IReadOnlyList<object> ExtraFields => [];

    /// <param name="internalView">Painel: inclui o custo unitário (margem).</param>
    /// <param name="accessToken">Só na resposta da criação.</param>
    public static OrderResponse From(Order o, bool internalView, string? accessToken = null)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new OrderResponse(
            o.Id.ToString(),
            [.. o.Items.Select(i => new OrderItemResponse(i.ProductId.ToString(), i.Name, i.VariantLabel, i.VariantId, i.Sku, i.UnitPriceCents,
                internalView ? i.UnitCostCents : null, i.Quantity))],
            o.SubtotalCents, o.DiscountCents, o.DeliveryFeeCents, o.GiftCardFeeCents, o.TotalCents,
            o.Customer.Name, o.Customer.Phone, o.Customer.Email, o.CustomerId?.ToString(),
            o.DeliveryMethod.ToString().ToLowerInvariant(), o.Address,
            o.DeliveryDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            o.DeliveryWindow?.ToString().ToLowerInvariant(), o.GiftCard, o.CouponCode,
            o.SubstitutionMode switch { Domain.Ordering.SubstitutionMode.AskMe => "ask_me", var m => m.ToString().ToLowerInvariant() },
            OrderStatusContract.ToContract(o.Status),
            [.. o.History.Select(h => new StatusHistoryResponse(h.From is { } f ? OrderStatusContract.ToContract(f) : null,
                OrderStatusContract.ToContract(h.To), CatalogMapping.Iso(h.At), h.ActorRole, h.Reason))],
            accessToken,
            o.PaymentStatus,
            o.PaymentInitializationStatus,
            o.PaymentRail,
            o.PixCode,
            CatalogMapping.Iso(o.CreatedAt),
            CatalogMapping.Iso(o.UpdatedAt),
            o.TierDiscountCents,
            o.CashbackUsedCents,
            o.RefundStatus,
            o.RefundedAmountCents)
        {
            PaymentMethod = o.PaymentMethod,
            Origin = o.Origin,
            Payments = [.. o.Payments.Select(p => new PosPaymentResponse(p.Method, p.AmountCents))],
            CashSessionId = internalView ? o.CashSessionId?.ToString() : null,
            Substitutions = o.Substitutions,
        };
    }
}

public sealed record PosPaymentResponse(string Method, long Amount);

public sealed record OrderEnvelope(OrderResponse Order);

public sealed record OrderPage(IReadOnlyList<OrderResponse> Orders, int Total, int Page, int Limit, int Pages);

public sealed record OrderItemInput(string? ProductId, int Quantity, string? VariantLabel);

public sealed record CheckoutInput(
    string? IdempotencyKey,
    string? CustomerName,
    string? CustomerPhone,
    string? CustomerEmail,
    IReadOnlyList<OrderItemInput>? Items,
    string? DeliveryMethod,
    DeliveryAddress? Address,
    string? DeliveryDate,
    string? DeliveryTimeWindow,
    GiftCard? GiftCard,
    string? CouponCode,
    string? SubstitutionMode,
    long? UseCashback = null);

public sealed record OrderListFilter(string? Status, string? Search, long? MinTotal, long? MaxTotal, int Page, int Limit);

/// <summary>Leituras de pedidos (lado Q do CQRS).</summary>
public interface IOrderQueries
{
    Task<OrderPage> ListAsync(OrderListFilter filter, Guid? customerId, CancellationToken cancellationToken);
}

public static class DeliveryMethodContract
{
    public static DeliveryMethod? Parse(string? value) => value switch
    {
        null or "entrega" => DeliveryMethod.Entrega,
        "retirada" => DeliveryMethod.Retirada,
        _ => null,
    };
}
