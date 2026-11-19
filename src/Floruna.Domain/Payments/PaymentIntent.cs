using Floruna.Domain.Ordering;
using Floruna.SharedKernel;

namespace Floruna.Domain.Payments;

/// <summary>Trilho de cobrança: PIX direto na chave da loja ou via gateway.</summary>
public enum PaymentRail
{
    PixOffline,
    MercadoPago,
}

/// <summary>D-009: estados próprios do pagamento, separados dos estados do pedido.</summary>
public enum PaymentIntentStatus
{
    Pending,
    Paid,
    Failed,
    Cancelled,
    Refunded,
}

/// <summary>Status normalizados do provedor (a ACL traduz o vocabulário do Mercado Pago para estes).</summary>
public enum ProviderPaymentStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
    Refunded,
}

public enum TransactionSource
{
    Webhook,
    Manual,
    Reconciliation,
}

/// <summary>Comprovante de um PIX confirmado à mão (D-010: confirmação manual é AUDITADA).</summary>
public sealed record PaymentEvidence(string Type, string Reference, string Note);

/// <summary>
/// Fato financeiro observado (append-only): "às 14:02, o provedor disse aprovado, R$ 125,50".
/// O status da intenção é derivado destes fatos; nada é sobrescrito sem deixar rastro.
/// </summary>
/// <remarks>A evidência fica "achatada" (tipo/referência/nota) para o documento JSON ser simples de ler e de consultar.</remarks>
public sealed record PaymentTransaction(
    ProviderPaymentStatus Status, TransactionSource Source, string ActorType, string? ActorId, string? ExternalId,
    string? EvidenceType, string? EvidenceReference, string? EvidenceNote, long AmountCents, DateTimeOffset ObservedAt, string IdempotencyKey)
{
    public PaymentEvidence? Evidence => EvidenceType is null ? null : new PaymentEvidence(EvidenceType, EvidenceReference ?? string.Empty, EvidenceNote ?? string.Empty);
}

/// <summary>
/// Intenção de pagamento de um pedido. Um pedido tem UMA intenção ativa; ela concentra o
/// trilho, o código PIX e o livro-razão do que o provedor (ou a loja) informou.
/// </summary>
public sealed class PaymentIntent : AggregateRoot<Guid>, ITenantOwned
{
    private readonly List<PaymentTransaction> _transactions = [];

    private PaymentIntent()
        : base(Guid.Empty)
    {
        PixCode = string.Empty;
        Currency = Money.DefaultCurrency;
    }

    private PaymentIntent(TenantId tenantId, OrderId orderId, long amountCents, PaymentRail rail, string pixCode, string? providerPaymentId, bool fallbackUsed, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        OrderId = orderId;
        AmountCents = amountCents;
        Currency = Money.DefaultCurrency;
        Rail = rail;
        PixCode = pixCode;
        ProviderPaymentId = providerPaymentId;
        FallbackUsed = fallbackUsed;
        Status = PaymentIntentStatus.Pending;
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public OrderId OrderId { get; private set; }

    public long AmountCents { get; private set; }

    public string Currency { get; private set; }

    public PaymentRail Rail { get; private set; }

    /// <summary>"Copia e cola" mostrado ao comprador.</summary>
    public string PixCode { get; private set; }

    public string? ProviderPaymentId { get; private set; }

    /// <summary>O gateway falhou e a loja recebeu PIX direto (contingência habilitada).</summary>
    public bool FallbackUsed { get; private set; }

    public PaymentIntentStatus Status { get; private set; }

    public long PaidAmountCents { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>
    /// Pagamento que não pôde virar pedido confirmado (ex.: pagou depois de o pedido ser
    /// cancelado por abandono). Fica marcado para a loja resolver (reembolso), NUNCA é perdido.
    /// </summary>
    public string? ReconciliationIssue { get; private set; }

    public IReadOnlyList<PaymentTransaction> Transactions => _transactions.AsReadOnly();

    public DateTimeOffset CreatedAt { get; private set; }

    public static PaymentIntent Open(TenantId tenantId, OrderId orderId, long amountCents, PaymentRail rail, string pixCode, string? providerPaymentId, bool fallbackUsed, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountCents);
        return new PaymentIntent(tenantId, orderId, amountCents, rail, pixCode, providerPaymentId, fallbackUsed, now);
    }

    /// <summary>
    /// Registra o que o provedor informou. Idempotente pela chave: a mesma notificação
    /// processada duas vezes não duplica o fato nem muda o estado de novo.
    /// </summary>
    /// <returns>True se o estado mudou para Pago agora (para quem chama confirmar o pedido).</returns>
    public Result<bool> RecordProviderStatus(ProviderPaymentStatus status, long amountCents, string idempotencyKey, DateTimeOffset now)
    {
        if (_transactions.Any(t => t.IdempotencyKey == idempotencyKey))
        {
            return false;
        }

        _transactions.Add(new PaymentTransaction(status, TransactionSource.Webhook, "integration", Rail.ToString(), ProviderPaymentId, null, null, null, amountCents, now, idempotencyKey));
        return Apply(status, amountCents, now);
    }

    /// <summary>
    /// Loja confirma que o PIX caiu (D-010). Exige o valor EXATO do pedido e uma evidência:
    /// confirmar por engano libera a montagem de um buquê não pago.
    /// </summary>
    public Result<bool> ConfirmManually(long amountCents, PaymentEvidence evidence, string? externalId, Guid? actorId, string idempotencyKey, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (_transactions.Any(t => t.IdempotencyKey == idempotencyKey))
        {
            return false;
        }

        if (amountCents != AmountCents)
        {
            return PaymentErrors.AmountMismatch(AmountCents);
        }

        if (Status == PaymentIntentStatus.Paid)
        {
            return PaymentErrors.AlreadyPaid;
        }

        _transactions.Add(new PaymentTransaction(ProviderPaymentStatus.Approved, TransactionSource.Manual, "staff", actorId?.ToString(), externalId,
            evidence.Type, evidence.Reference, evidence.Note, amountCents, receivedAt, idempotencyKey));
        return Apply(ProviderPaymentStatus.Approved, amountCents, receivedAt);
    }

    public void FlagReconciliationIssue(string issue) => ReconciliationIssue ??= issue;

    private Result<bool> Apply(ProviderPaymentStatus status, long amountCents, DateTimeOffset now)
    {
        switch (status)
        {
            case ProviderPaymentStatus.Approved when Status != PaymentIntentStatus.Paid:
                if (amountCents < AmountCents)
                {
                    // Pagou menos que o pedido: não confirma; fica para a loja olhar.
                    FlagReconciliationIssue("underpaid");
                    return false;
                }

                Status = PaymentIntentStatus.Paid;
                PaidAmountCents = amountCents;
                PaidAt = now;
                Raise(new PaymentConfirmed(Id, OrderId, TenantId, now));
                return true;
            case ProviderPaymentStatus.Rejected when Status == PaymentIntentStatus.Pending:
                Status = PaymentIntentStatus.Failed;
                return false;
            case ProviderPaymentStatus.Cancelled when Status == PaymentIntentStatus.Pending:
                Status = PaymentIntentStatus.Cancelled;
                return false;
            case ProviderPaymentStatus.Refunded when Status == PaymentIntentStatus.Paid:
                Status = PaymentIntentStatus.Refunded;
                return false;
            default:
                return false; // transição sem efeito (ex.: "pendente" depois de "pago"): o fato fica no livro, o estado não regride
        }
    }
}

public sealed record PaymentConfirmed(Guid IntentId, OrderId OrderId, TenantId TenantId, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

/// <summary>Configuração de pagamento da loja (o token do gateway fica CIFRADO).</summary>
/// <remarks>Uma por loja (índice único em <c>tenant_id</c>).</remarks>
public sealed class PaymentSettings : Entity<Guid>, ITenantOwned
{
    private PaymentSettings()
        : base(Guid.Empty)
    {
        PixKey = string.Empty;
        PixReceiverName = string.Empty;
        PixCity = string.Empty;
    }

    private PaymentSettings(TenantId tenantId)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        PixKey = string.Empty;
        PixReceiverName = string.Empty;
        PixCity = string.Empty;
    }

    public TenantId TenantId { get; private set; }

    public bool UseMercadoPago { get; private set; }

    public bool OfflineFallbackEnabled { get; private set; }

    public string PixKey { get; private set; }

    public string PixReceiverName { get; private set; }

    public string PixCity { get; private set; }

    /// <summary>Token do Mercado Pago cifrado (AES-256-GCM). Nunca sai da API em claro.</summary>
    public string? MercadoPagoTokenCiphertext { get; private set; }

    public static PaymentSettings For(TenantId tenantId) => new(tenantId);

    public void Update(bool useMercadoPago, bool offlineFallback, string? pixKey, string? receiverName, string? city)
    {
        UseMercadoPago = useMercadoPago;
        OfflineFallbackEnabled = offlineFallback;
        PixKey = pixKey?.Trim() ?? string.Empty;
        PixReceiverName = receiverName?.Trim() ?? string.Empty;
        PixCity = city?.Trim() ?? string.Empty;
    }

    public void SetMercadoPagoToken(string? ciphertext) => MercadoPagoTokenCiphertext = ciphertext;
}

/// <summary>
/// Inbox de notificações do gateway: registra CADA aviso recebido (auditoria) e gera um evento
/// para a outbox processar com retry. O conteúdo NÃO é confiável: só o ID é usado, e o status
/// real é consultado no provedor ("verify by fetch").
/// </summary>
public sealed class PaymentNotification : AggregateRoot<Guid>, ITenantOwned
{
    private PaymentNotification()
        : base(Guid.Empty)
    {
        Provider = string.Empty;
        ProviderPaymentId = string.Empty;
    }

    private PaymentNotification(TenantId tenantId, string provider, string providerPaymentId, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        Provider = provider;
        ProviderPaymentId = providerPaymentId;
        ReceivedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public string Provider { get; private set; }

    public string ProviderPaymentId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public static PaymentNotification Receive(TenantId tenantId, string provider, string providerPaymentId, DateTimeOffset now)
    {
        var notification = new PaymentNotification(tenantId, provider, providerPaymentId, now);
        notification.Raise(new PaymentNotificationReceived(notification.Id, tenantId, provider, providerPaymentId, now));
        return notification;
    }
}

public sealed record PaymentNotificationReceived(Guid NotificationId, TenantId TenantId, string Provider, string ProviderPaymentId, DateTimeOffset OccurredAt)
    : DomainEvent(OccurredAt);

public static class PaymentErrors
{
    public static readonly Error IntentNotFound = Error.NotFound("NOT_FOUND", "Intenção de pagamento não encontrada.");
    public static readonly Error AlreadyPaid = Error.Conflict("CONFLICT", "Este pagamento já foi confirmado.");
    public static readonly Error GatewayNotAllowed = Error.Forbidden("PLAN_UPGRADE_REQUIRED", "Seu plano não inclui pagamento por gateway.");

    public static Error AmountMismatch(long expected) => Error.Validation(
        "VALIDATION_ERROR", $"O valor confirmado deve ser exatamente o total do pedido ({expected} centavos).");
}

public interface IPaymentRepository
{
    void Add(PaymentIntent intent);

    void Add(PaymentNotification notification);

    void Add(PaymentSettings settings);

    Task<PaymentIntent?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<PaymentIntent?> FindByOrderAsync(OrderId orderId, CancellationToken cancellationToken);

    Task<PaymentIntent?> FindByProviderPaymentIdAsync(string providerPaymentId, CancellationToken cancellationToken);

    Task<PaymentSettings?> GetSettingsAsync(CancellationToken cancellationToken);
}
