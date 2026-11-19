using Floruna.Domain.Ordering;
using Floruna.SharedKernel;

namespace Floruna.Domain.Payments;

public enum RefundScope
{
    /// <summary>Cancelamento de pedido pago: devolve tudo o que resta.</summary>
    Full,

    /// <summary>Pós-venda (devolução, cortesia): parte do valor, com ou sem itens.</summary>
    Partial,
}

/// <summary>Como o dinheiro volta: pelo gateway, ou pela própria loja (PIX direto) e o sistema só registra.</summary>
public enum RefundMethod
{
    Gateway,
    Manual,
}

public enum RefundStatus
{
    /// <summary>Reservado e sem resposta definitiva (inclui "não sei se o gateway processou").</summary>
    Pending,
    Succeeded,

    /// <summary>Recusado DEFINITIVAMENTE: a reserva é devolvida.</summary>
    Failed,
}

/// <summary>Item devolvido (subconjunto do pedido). Os itens do pedido em si nunca mudam: são o registro da venda.</summary>
public sealed record ReturnedItem(string ProductId, string Name, int Quantity, long UnitCostCents);

/// <summary>
/// Um reembolso é uma SAGA com efeito externo (devolver dinheiro), e o efeito externo não entra
/// em transação. Passos:
/// <list type="number">
/// <item>Reserva (transação curta, intenção travada): o valor sai do "reembolsável".</item>
/// <item>Chamada ao gateway FORA da transação, com a chave de idempotência = id do reembolso.</item>
/// <item>Conclusão (outra transação): sucesso consolida; recusa definitiva devolve a reserva.</item>
/// </list>
/// A diferença que importa: <b>recusa</b> é definitiva, <b>timeout</b> não é. Num timeout o dinheiro
/// PODE ter saído; marcar como falha e liberar a reserva permitiria reembolsar de novo. Então o
/// reembolso fica pendente e o job repete a MESMA chamada (mesma chave: o gateway não duplica).
/// </summary>
public sealed class Refund : AggregateRoot<Guid>, ITenantOwned
{
    public const int MaxAttempts = 10;
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private readonly List<ReturnedItem> _items = [];

    private Refund()
        : base(Guid.Empty)
    {
        Reason = string.Empty;
        IdempotencyKey = string.Empty;
        RequestedBy = string.Empty;
    }

    private Refund(TenantId tenantId, Guid intentId, OrderId orderId, long amountCents, RefundScope scope, RefundMethod method, string reason,
        string idempotencyKey, IEnumerable<ReturnedItem> items, bool restock, string requestedBy, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        IntentId = intentId;
        OrderId = orderId;
        AmountCents = amountCents;
        Scope = scope;
        Method = method;
        Reason = reason;
        IdempotencyKey = idempotencyKey;
        _items.AddRange(items);
        Restock = restock;
        RequestedBy = requestedBy;
        Status = RefundStatus.Pending;
        CreatedAt = now;
        NextAttemptAt = now;
        LeaseUntil = now.Add(Lease); // a tentativa síncrona tem a vez; o job só pega se ela travar
    }

    public TenantId TenantId { get; private set; }

    public Guid IntentId { get; private set; }

    public OrderId OrderId { get; private set; }

    public long AmountCents { get; private set; }

    public RefundScope Scope { get; private set; }

    public RefundMethod Method { get; private set; }

    public string Reason { get; private set; }

    /// <summary>Chave do PEDIDO de reembolso (cliente/painel). A chave enviada ao gateway é o <see cref="Entity{TId}.Id"/>.</summary>
    public string IdempotencyKey { get; private set; }

    public IReadOnlyList<ReturnedItem> Items => _items.AsReadOnly();

    /// <summary>Os itens devolvidos voltam ao estoque? (Falso para flor danificada: regra floral 7.)</summary>
    public bool Restock { get; private set; }

    public string RequestedBy { get; private set; }

    public RefundStatus Status { get; private set; }

    public string? ProviderRefundId { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Esgotou as tentativas sem resposta: precisa de gente (conferir no painel do gateway).</summary>
    public bool NeedsAttention => Status == RefundStatus.Pending && Attempts >= MaxAttempts;

    public static Refund Request(TenantId tenantId, Guid intentId, OrderId orderId, long amountCents, RefundScope scope, RefundMethod method,
        string reason, string idempotencyKey, IEnumerable<ReturnedItem> items, bool restock, string requestedBy, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountCents);
        var refund = new Refund(tenantId, intentId, orderId, amountCents, scope, method, reason, idempotencyKey, items, restock, requestedBy, now);
        refund.Raise(new RefundRequested(refund.Id, orderId, tenantId, now));
        return refund;
    }

    /// <returns>false se já estava concluído (outra réplica terminou antes).</returns>
    public bool Succeed(string? providerRefundId, DateTimeOffset now)
    {
        if (Status != RefundStatus.Pending)
        {
            return false;
        }

        Status = RefundStatus.Succeeded;
        ProviderRefundId = providerRefundId;
        CompletedAt = now;
        LeaseUntil = null;
        Raise(new RefundSucceeded(Id, OrderId, TenantId, Scope, now));
        return true;
    }

    public bool Fail(string error, DateTimeOffset now)
    {
        if (Status != RefundStatus.Pending)
        {
            return false;
        }

        Status = RefundStatus.Failed;
        LastError = Truncate(error);
        CompletedAt = now;
        LeaseUntil = null;
        return true;
    }

    /// <summary>Resultado desconhecido: continua pendente, com backoff exponencial (30 s, 60 s, ... até 1 h).</summary>
    public void Defer(string error, DateTimeOffset now)
    {
        Attempts++;
        LastError = Truncate(error);
        var delay = TimeSpan.FromTicks(Math.Min(BaseBackoff.Ticks * (1L << Math.Min(Attempts - 1, 20)), MaxBackoff.Ticks));
        NextAttemptAt = now.Add(delay);
        LeaseUntil = null;
    }

    private static string Truncate(string error) => error.Length > 500 ? error[..500] : error;
}

public sealed record RefundRequested(Guid RefundId, OrderId OrderId, TenantId TenantId, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed record RefundSucceeded(Guid RefundId, OrderId OrderId, TenantId TenantId, RefundScope Scope, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public interface IRefundRepository
{
    void Add(Refund refund);

    Task<Refund?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Refund?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Reembolso travado e lido do banco (descarta cópia rastreada).</summary>
    Task<Refund?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Refund>> ByIntentAsync(Guid intentId, CancellationToken cancellationToken);

    /// <summary>Intenção travada (<c>FOR UPDATE</c>): reservas concorrentes entram em fila.</summary>
    Task<PaymentIntent?> GetIntentForUpdateAsync(Guid intentId, CancellationToken cancellationToken);

    /// <summary>
    /// Reivindica a vez de tentar (lease) com um UPDATE condicional: só uma réplica (ou a
    /// requisição síncrona) chama o gateway por vez para o mesmo reembolso.
    /// </summary>
    Task<bool> TryClaimAsync(Guid refundId, DateTimeOffset now, CancellationToken cancellationToken);
}
