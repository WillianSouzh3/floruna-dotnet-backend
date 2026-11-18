using Floruna.Domain.Catalog;
using Floruna.SharedKernel;

namespace Floruna.Domain.Inventory;

public enum ReservationState
{
    Held,
    Committed,
    Released,
    Expired,
}

public enum ReservationOwnerType
{
    Order,
    Manual,
}

/// <summary>
/// Reserva de estoque para um dono (pedido) por tempo limitado (RN-24).
/// Máquina de estados: <c>held → committed | released | expired</c>. As
/// transições finais são idempotentes: liberar duas vezes não devolve em dobro.
/// </summary>
public sealed class StockReservation : AggregateRoot<Guid>, ITenantOwned
{
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMinutes(30);

    private StockReservation()
        : base(Guid.Empty)
    {
        OwnerId = string.Empty;
        IdempotencyKey = string.Empty;
        Note = string.Empty;
    }

    private StockReservation(TenantId tenantId, ProductId productId, int quantity, ReservationOwnerType ownerType, string ownerId, string idempotencyKey, DateTimeOffset expiresAt)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        ProductId = productId;
        Quantity = quantity;
        OwnerType = ownerType;
        OwnerId = ownerId;
        IdempotencyKey = idempotencyKey;
        ExpiresAt = expiresAt;
        State = ReservationState.Held;
        Note = string.Empty;
    }

    public TenantId TenantId { get; private set; }

    public ProductId ProductId { get; private set; }

    public int Quantity { get; private set; }

    public ReservationOwnerType OwnerType { get; private set; }

    public string OwnerId { get; private set; }

    public string IdempotencyKey { get; private set; }

    public ReservationState State { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public string Note { get; private set; }

    public static StockReservation Hold(TenantId tenantId, ProductId productId, int quantity, ReservationOwnerType ownerType, string ownerId, string idempotencyKey, DateTimeOffset expiresAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return new StockReservation(tenantId, productId, quantity, ownerType, ownerId, idempotencyKey, expiresAt);
    }

    /// <summary>Encerra a reserva SEM vender (cliente desistiu, pagamento não veio). True = mudou agora.</summary>
    public bool Release(string note, DateTimeOffset now) => Settle(ReservationState.Released, note, now);

    /// <summary>O prazo passou sem o pedido concluir.</summary>
    public bool Expire(DateTimeOffset now) => now >= ExpiresAt && Settle(ReservationState.Expired, "prazo vencido", now);

    /// <summary>A reserva virou venda.</summary>
    public bool Commit(DateTimeOffset now) => Settle(ReservationState.Committed, string.Empty, now);

    private bool Settle(ReservationState target, string note, DateTimeOffset now)
    {
        if (State != ReservationState.Held)
        {
            return false; // idempotente: já encerrada não muda de novo
        }

        State = target;
        SettledAt = now;
        Note = note;
        return true;
    }
}

public interface IInventoryRepository
{
    void Add(StockItem item);

    void Add(StockMovement movement);

    void Add(StockReservation reservation);

    /// <summary>
    /// Carrega o estoque do produto TRAVANDO a linha até o fim da transação
    /// (<c>SELECT ... FOR UPDATE</c>). Exige transação aberta.
    /// </summary>
    Task<StockItem?> GetForUpdateAsync(ProductId productId, CancellationToken cancellationToken);

    Task<StockReservation?> GetReservationAsync(Guid id, CancellationToken cancellationToken);

    Task<StockReservation?> FindReservationByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);
}
