using System.Security.Cryptography;
using System.Text;
using Floruna.Domain.Catalog;
using Floruna.Domain.Delivery;
using Floruna.SharedKernel;

namespace Floruna.Domain.Ordering;

/// <summary>Contrato: <c>pendente, confirmado, em_preparo, em_entrega, entregue, cancelado</c>.</summary>
public enum OrderStatus
{
    Pendente,
    Confirmado,
    EmPreparo,
    EmEntrega,
    Entregue,
    Cancelado,
}

/// <summary>O que fazer se faltar flor (D-015). Contrato: <c>exact, preapproved, ask_me</c>.</summary>
public enum SubstitutionMode
{
    Exact,
    Preapproved,
    AskMe,
}

public readonly record struct OrderId(Guid Value)
{
    public static OrderId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Item com SNAPSHOT (RN-43): nome, variação, preço e custo COMO ERAM na compra.
/// Mudar o catálogo depois não altera pedidos antigos, e o lucro do período usa o
/// custo congelado aqui, não o custo de hoje.
/// </summary>
public sealed record OrderItem(
    ProductId ProductId, string? VariantId, string? VariantLabel, string Name, string? Sku,
    long UnitPriceCents, long UnitCostCents, int Quantity)
{
    public long LineTotalCents => UnitPriceCents * Quantity;
}

public sealed record DeliveryAddress(
    string RecipientName, string Street, string Number, string Complement, string Neighborhood,
    string City, string State, string ZipCode, string DeliveryNotes);

public sealed record GiftCard(string Message, string From, string To);

public sealed record CustomerContact(string Name, string Phone, string? Email);

/// <summary>Uma transição registrada (D-007): de onde, para onde, quem, por quê e com qual chave.</summary>
public sealed record StatusChange(
    OrderStatus? From, OrderStatus To, DateTimeOffset At, Guid? ActorId, string ActorRole, string Reason, string? IdempotencyKey);

/// <summary>Valores calculados no SERVIDOR (RN-40): o cliente nunca informa preço.</summary>
/// <param name="DiscountCents">Desconto na MERCADORIA: cupom + nível de fidelidade (contrato do Nest).</param>
/// <param name="CashbackUsedCents">Saldo de cashback usado: é forma de pagamento, não desconto.</param>
/// <param name="TierDiscountCents">Parte do desconto que veio do nível (já incluída em <paramref name="DiscountCents"/>).</param>
public sealed record OrderTotals(long SubtotalCents, long DiscountCents, long DeliveryFeeCents, long GiftCardFeeCents, long CashbackUsedCents = 0, long TierDiscountCents = 0)
{
    public long TotalCents => SubtotalCents - DiscountCents + DeliveryFeeCents + GiftCardFeeCents - CashbackUsedCents;
}

/// <summary>Uma parcela do pagamento no balcão (pagamento dividido). Contrato: <c>dinheiro, pix, cartao_debito, cartao_credito, outro</c>.</summary>
public sealed record PosPayment(string Method, long AmountCents)
{
    public static readonly IReadOnlySet<string> Methods = new HashSet<string>(StringComparer.Ordinal)
    {
        "dinheiro", "pix", "cartao_debito", "cartao_credito", "outro",
    };

    public const string Cash = "dinheiro";
}

/// <param name="CashSessionId">Caixa aberto do operador; null = venda sem caixa (entra no relatório, não na conferência).</param>
public sealed record PosPlacement(
    TenantId TenantId, string? IdempotencyKey, CustomerContact Customer, IReadOnlyList<OrderItem> Items, long DiscountCents,
    IReadOnlyList<PosPayment> Payments, Guid OperatorId, Guid? CashSessionId);

public sealed record OrderPlacement(
    TenantId TenantId,
    string? IdempotencyKey,
    CustomerContact Customer,
    Guid? CustomerId,
    IReadOnlyList<OrderItem> Items,
    OrderTotals Totals,
    DeliveryMethod DeliveryMethod,
    DeliveryAddress? Address,
    DateOnly? DeliveryDate,
    DeliveryWindow? DeliveryWindow,
    Guid? DeliveryZoneId,
    Guid? DeliverySlotScope,
    GiftCard? GiftCard,
    Guid? CouponId,
    string? CouponCode,
    SubstitutionMode SubstitutionMode);

/// <summary>
/// Pedido. Raiz de agregado com máquina de estados (RN-45): avanços podem pular
/// etapas (a operação real nem sempre registra cada clique), mas nunca voltam;
/// cancelar só a partir de estados não finais.
/// </summary>
public sealed class Order : AggregateRoot<OrderId>, ITenantOwned
{
    /// <summary>Prazo para pagar/confirmar. Depois disso o pedido é abandonado e o estoque volta.</summary>
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromHours(3);

    private static readonly IReadOnlyDictionary<OrderStatus, int> Rank = new Dictionary<OrderStatus, int>
    {
        [OrderStatus.Pendente] = 0,
        [OrderStatus.Confirmado] = 1,
        [OrderStatus.EmPreparo] = 2,
        [OrderStatus.EmEntrega] = 3,
        [OrderStatus.Entregue] = 4,
    };

    private readonly List<OrderItem> _items = [];
    private readonly List<StatusChange> _history = [];
    private readonly List<PosPayment> _payments = [];

    private Order()
        : base(default)
    {
        Customer = null!;
        AccessTokenHash = string.Empty;
    }

    private Order(OrderId id, OrderPlacement p, DateTimeOffset now)
        : base(id)
    {
        TenantId = p.TenantId;
        IdempotencyKey = p.IdempotencyKey;
        Customer = p.Customer;
        CustomerId = p.CustomerId;
        _items.AddRange(p.Items);
        SubtotalCents = p.Totals.SubtotalCents;
        DiscountCents = p.Totals.DiscountCents;
        DeliveryFeeCents = p.Totals.DeliveryFeeCents;
        GiftCardFeeCents = p.Totals.GiftCardFeeCents;
        TierDiscountCents = p.Totals.TierDiscountCents;
        CashbackUsedCents = p.Totals.CashbackUsedCents;
        TotalCents = p.Totals.TotalCents;
        DeliveryMethod = p.DeliveryMethod;
        Address = p.Address;
        DeliveryDate = p.DeliveryDate;
        DeliveryWindow = p.DeliveryWindow;
        DeliveryZoneId = p.DeliveryZoneId;
        DeliverySlotScope = p.DeliverySlotScope;
        GiftCard = p.GiftCard;
        CouponId = p.CouponId;
        CouponCode = p.CouponCode;
        SubstitutionMode = p.SubstitutionMode;
        Status = OrderStatus.Pendente;
        CreatedAt = now;
        UpdatedAt = now;
        PaymentDeadline = now.Add(PaymentWindow);
        AccessTokenHash = string.Empty;
    }

    public TenantId TenantId { get; private set; }

    public string? IdempotencyKey { get; private set; }

    public CustomerContact Customer { get; private set; }

    public Guid? CustomerId { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items.AsReadOnly();

    public long SubtotalCents { get; private set; }

    public long DiscountCents { get; private set; }

    public long DeliveryFeeCents { get; private set; }

    public long GiftCardFeeCents { get; private set; }

    public long TotalCents { get; private set; }

    public long TierDiscountCents { get; private set; }

    public long CashbackUsedCents { get; private set; }

    /// <summary>Carteira de onde saiu o cashback usado (pode não ser a do e-mail do pedido).</summary>
    public Guid? LoyaltyAccountId { get; private set; }

    /// <summary>Guardião do acúmulo: o cashback do pedido é creditado UMA vez.</summary>
    public DateTimeOffset? LoyaltyAccruedAt { get; private set; }

    /// <summary>Guardião do estorno do resgate.</summary>
    public DateTimeOffset? LoyaltyReversedAt { get; private set; }

    /// <summary>Desconto do cupom (o total menos a parte do nível).</summary>
    public long CouponDiscountCents => DiscountCents - TierDiscountCents;

    public DeliveryMethod DeliveryMethod { get; private set; }

    public DeliveryAddress? Address { get; private set; }

    public DateOnly? DeliveryDate { get; private set; }

    public DeliveryWindow? DeliveryWindow { get; private set; }

    public Guid? DeliveryZoneId { get; private set; }

    /// <summary>Escopo em que a vaga da janela foi contada (para devolvê-la no cancelamento).</summary>
    public Guid? DeliverySlotScope { get; private set; }

    public GiftCard? GiftCard { get; private set; }

    public Guid? CouponId { get; private set; }

    public string? CouponCode { get; private set; }

    public SubstitutionMode SubstitutionMode { get; private set; }

    public OrderStatus Status { get; private set; }

    public IReadOnlyList<StatusChange> History => _history.AsReadOnly();

    /// <summary>Hash do token de leitura pública (RN-47). O token em si só existe na resposta da criação.</summary>
    public string AccessTokenHash { get; private set; }

    public DateTimeOffset PaymentDeadline { get; private set; }

    // Visão do pagamento no pedido (contrato do Nest). A fonte da verdade é o PaymentIntent;
    // estes campos são uma cópia para a página do pedido não precisar de outra consulta.

    /// <summary>Contrato: <c>pending</c>, <c>approved</c>, <c>rejected</c>, <c>cancelled</c>.</summary>
    public string PaymentStatus { get; private set; } = "pending";

    /// <summary>Contrato: <c>pending</c>, <c>ready</c>, <c>failed</c>.</summary>
    public string PaymentInitializationStatus { get; private set; } = "pending";

    public string? PaymentRail { get; private set; }

    /// <summary>Contrato: <c>not_required</c>, <c>processing</c>, <c>refunded</c>, <c>failed</c> (reembolso INTEGRAL do cancelamento).</summary>
    public string RefundStatus { get; private set; } = "not_required";

    /// <summary>Cópia do total devolvido (fonte da verdade: a intenção de pagamento).</summary>
    public long RefundedAmountCents { get; private set; }

    public string PixCode { get; private set; } = string.Empty;

    /// <summary>Contrato: <c>online</c> (vitrine) ou <c>pdv</c> (balcão).</summary>
    public string Origin { get; private set; } = "online";

    /// <summary>Contrato: <c>pix</c> no online; no balcão, a forma usada ou <c>multiplo</c>.</summary>
    public string PaymentMethod { get; private set; } = "pix";

    public IReadOnlyList<PosPayment> Payments => _payments.AsReadOnly();

    public Guid? OperatorId { get; private set; }

    public Guid? CashSessionId { get; private set; }

    /// <summary>Parte paga em DINHEIRO (desnormalizada para a conferência do caixa somar por índice).</summary>
    public long CashReceivedCents { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Cria o pedido. Devolve também o token de acesso público EM CLARO (só desta vez):
    /// o banco guarda o hash, então um vazamento da tabela não abre a página dos pedidos.
    /// </summary>
    public static Result<(Order Order, string AccessToken)> Place(OrderId id, OrderPlacement placement, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.Items.Count == 0)
        {
            return OrderErrors.EmptyCart;
        }

        if (placement.DeliveryMethod == DeliveryMethod.Entrega && placement.Address is null)
        {
            return OrderErrors.AddressRequired;
        }

        if (placement.Totals.DiscountCents > placement.Totals.SubtotalCents)
        {
            return OrderErrors.DiscountAboveItems; // RN-35
        }

        if (placement.Totals.TotalCents <= 0)
        {
            return OrderErrors.TotalMustBePositive; // RN-34: PIX de valor zero é cobrança inválida
        }

        var order = new Order(id, placement, now);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        order.AccessTokenHash = HashToken(token);
        order._history.Add(new StatusChange(null, OrderStatus.Pendente, now, placement.CustomerId, "customer", "Pedido criado", null));
        order.Raise(new OrderPlaced(id, placement.TenantId, now));
        return (order, token);
    }

    /// <summary>
    /// Venda no balcão (PDV não fiscal): o cliente leva na hora, então o pedido já nasce PAGO e
    /// ENTREGUE. A soma das formas de pagamento precisa bater com o total, centavo a centavo.
    /// </summary>
    public static Result<(Order Order, string AccessToken)> PlacePos(OrderId id, PosPlacement p, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.Payments.Count is 0 or > 5 || p.Payments.Any(x => !PosPayment.Methods.Contains(x.Method) || x.AmountCents < 0))
        {
            return Error.Validation("VALIDATION_ERROR", "Formas de pagamento inválidas (1 a 5, valores não negativos).");
        }

        var subtotal = p.Items.Sum(i => i.LineTotalCents);
        var totals = new OrderTotals(subtotal, p.DiscountCents, 0, 0);
        var paid = p.Payments.Sum(x => x.AmountCents);
        if (p.Items.Count > 0 && p.DiscountCents <= subtotal && totals.TotalCents > 0 && paid != totals.TotalCents)
        {
            return Error.Validation("VALIDATION_ERROR", $"A soma das formas de pagamento ({paid}) não bate com o total ({totals.TotalCents}).");
        }

        var placed = Place(id, new OrderPlacement(p.TenantId, p.IdempotencyKey, p.Customer, null, p.Items, totals, DeliveryMethod.Retirada,
            null, null, null, null, null, null, null, null, SubstitutionMode.Exact), now);
        if (placed.IsFailure)
        {
            return placed;
        }

        var order = placed.Value.Order;
        order.Origin = "pdv";
        order._payments.AddRange(p.Payments);
        order.PaymentMethod = p.Payments.Count == 1 ? p.Payments[0].Method : "multiplo";
        order.CashReceivedCents = p.Payments.Where(x => x.Method == PosPayment.Cash).Sum(x => x.AmountCents);
        order.OperatorId = p.OperatorId;
        order.CashSessionId = p.CashSessionId;
        order.PaymentStatus = "approved";
        order.PaymentInitializationStatus = "ready";
        order.Status = OrderStatus.Entregue;
        order._history.Clear();
        order._history.Add(new StatusChange(null, OrderStatus.Entregue, now, p.OperatorId, "pdv", "Venda no balcão", null));
        return placed;
    }

    /// <summary>Comparação em tempo constante: o tempo de resposta não ajuda a adivinhar o token.</summary>
    public bool CanBeReadWith(string? token) =>
        !string.IsNullOrEmpty(token) &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashToken(token)), Encoding.ASCII.GetBytes(AccessTokenHash));

    /// <summary>
    /// Transição de estado (RN-45, RN-46). Com <paramref name="idempotencyKey"/> repetida,
    /// devolve sucesso SEM mudar nada (o mesmo clique enviado duas vezes). Com
    /// <paramref name="expectedState"/> diferente do atual, falha (compare-and-swap: outra
    /// pessoa mexeu no pedido antes).
    /// </summary>
    /// <returns>A mudança registrada, ou null se foi um replay idempotente.</returns>
    public Result<StatusChange?> ChangeStatus(OrderStatus target, Guid? actorId, string actorRole, string? reason, OrderStatus? expectedState, string? idempotencyKey, DateTimeOffset now)
    {
        if (idempotencyKey is not null && _history.Any(h => h.IdempotencyKey == idempotencyKey))
        {
            return Result.Success<StatusChange?>(null);
        }

        if (expectedState is { } expected && expected != Status)
        {
            return OrderErrors.UnexpectedState(Status);
        }

        if (!CanMove(Status, target))
        {
            return OrderErrors.InvalidTransition(Status, target);
        }

        var change = new StatusChange(Status, target, now, actorId, actorRole, reason?.Trim() ?? string.Empty, idempotencyKey);
        _history.Add(change);
        Status = target;
        UpdatedAt = now;
        Raise(new OrderStatusChanged(Id, TenantId, change.From!.Value, target, now));
        return change;
    }

    public void PaymentReady(string rail, string pixCode, DateTimeOffset now)
    {
        PaymentRail = rail;
        PixCode = pixCode;
        PaymentInitializationStatus = "ready";
        UpdatedAt = now;
    }

    public void PaymentInitializationFailed(DateTimeOffset now)
    {
        PaymentInitializationStatus = "failed";
        UpdatedAt = now;
    }

    public void PaymentStatusChanged(string status, DateTimeOffset now)
    {
        PaymentStatus = status;
        UpdatedAt = now;
    }

    public void LinkLoyaltyAccount(Guid accountId) => LoyaltyAccountId = accountId;

    /// <returns>false se já foi acumulado (replay da outbox).</returns>
    public bool MarkLoyaltyAccrued(DateTimeOffset now)
    {
        if (LoyaltyAccruedAt is not null)
        {
            return false;
        }

        LoyaltyAccruedAt = now;
        return true;
    }

    /// <returns>false se já foi estornado.</returns>
    public bool MarkLoyaltyReversed(DateTimeOffset now)
    {
        if (LoyaltyReversedAt is not null)
        {
            return false;
        }

        LoyaltyReversedAt = now;
        return true;
    }

    /// <summary>
    /// Reflete o reembolso no pedido. Recebe o total ABSOLUTO lido da intenção travada (não soma
    /// em cima do valor em memória): uma cópia velha do pedido não consegue perder um reembolso.
    /// </summary>
    public void RefundChanged(string refundStatus, long refundedTotalCents, bool fullyRefunded, DateTimeOffset now)
    {
        RefundStatus = refundStatus;
        RefundedAmountCents = refundedTotalCents;
        if (fullyRefunded)
        {
            PaymentStatus = "refunded";
        }

        UpdatedAt = now;
    }

    public bool IsAbandonedAt(DateTimeOffset now) => Status == OrderStatus.Pendente && now >= PaymentDeadline;

    public static bool CanMove(OrderStatus from, OrderStatus to)
    {
        if (to == OrderStatus.Cancelado)
        {
            return from is not (OrderStatus.Entregue or OrderStatus.Cancelado);
        }

        return Rank.TryGetValue(from, out var fromRank) && Rank.TryGetValue(to, out var toRank) && toRank > fromRank;
    }

    private static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed record OrderPlaced(OrderId OrderId, TenantId TenantId, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed record OrderStatusChanged(OrderId OrderId, TenantId TenantId, OrderStatus From, OrderStatus To, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public static class OrderStatusContract
{
    public static string ToContract(OrderStatus status) => status switch
    {
        OrderStatus.EmPreparo => "em_preparo",
        OrderStatus.EmEntrega => "em_entrega",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static OrderStatus? Parse(string? value) => value switch
    {
        "pendente" => OrderStatus.Pendente,
        "confirmado" => OrderStatus.Confirmado,
        "em_preparo" => OrderStatus.EmPreparo,
        "em_entrega" => OrderStatus.EmEntrega,
        "entregue" => OrderStatus.Entregue,
        "cancelado" => OrderStatus.Cancelado,
        _ => null,
    };

    public static string Label(OrderStatus status) => status switch
    {
        OrderStatus.Pendente => "Pendente",
        OrderStatus.Confirmado => "Confirmado",
        OrderStatus.EmPreparo => "Em preparo",
        OrderStatus.EmEntrega => "Em entrega",
        OrderStatus.Entregue => "Entregue",
        _ => "Cancelado",
    };
}

public static class OrderErrors
{
    public static readonly Error NotFound = Error.NotFound("NOT_FOUND", "Pedido não encontrado.");
    public static readonly Error EmptyCart = Error.Validation("VALIDATION_ERROR", "O carrinho está vazio.", ["O carrinho está vazio."]);
    public static readonly Error AddressRequired = Error.Validation("VALIDATION_ERROR", "Informe o endereço de entrega.", ["Informe o endereço de entrega."]);
    public static readonly Error DiscountAboveItems = Error.Validation("VALIDATION_ERROR", "Desconto maior que o valor dos itens.");
    public static readonly Error TotalMustBePositive = Error.Validation("VALIDATION_ERROR", "O total do pedido precisa ser maior que zero.");
    public static readonly Error ReadRequiresToken = Error.Unauthorized("AUTHENTICATION_REQUIRED", "Informe o token do pedido ou autentique-se.");

    public static Error UnexpectedState(OrderStatus current) =>
        Error.Conflict("CONFLICT", $"O pedido mudou de estado (agora: {OrderStatusContract.ToContract(current)}). Atualize e tente de novo.");

    public static Error InvalidTransition(OrderStatus from, OrderStatus to) =>
        Error.Conflict("CONFLICT", $"Não é possível mudar o pedido de \"{OrderStatusContract.Label(from)}\" para \"{OrderStatusContract.Label(to)}\".");

    public static Error ProductUnavailable(string id) => Error.Conflict("CONFLICT", $"Produto indisponível: {id}.");

    public static Error VariantUnavailable(string product, string variant) => Error.Conflict("CONFLICT", $"Variação indisponível: {product} ({variant}).");

    public static Error VariantRequired(string product) => Error.Validation("VALIDATION_ERROR", $"Selecione uma variação para \"{product}\".");

    public static Error InsufficientStock(string product) => Error.Conflict("CONFLICT", $"Estoque insuficiente para {product}.");
}

public interface IOrderRepository
{
    void Add(Order order);

    Task<Order?> GetAsync(OrderId id, CancellationToken cancellationToken);

    Task<Order?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>Pedidos pendentes com prazo vencido, de TODAS as lojas (job): (loja, pedido).</summary>
    Task<IReadOnlyList<(TenantId TenantId, OrderId OrderId)>> AbandonedAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}
