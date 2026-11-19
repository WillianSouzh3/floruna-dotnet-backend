using Floruna.Domain.Catalog;
using Floruna.SharedKernel;

namespace Floruna.Domain.Inventory;

/// <summary>Tipos do ledger. Os nomes de contrato são os do Nest (<c>venda</c>, <c>entrada</c>...).</summary>
public enum StockMovementType
{
    Venda,
    Entrada,
    Ajuste,
    Perda,
    OpeningBalance,

    /// <summary>Item devolvido pelo cliente que voltou ao estoque (reembolso com <c>restock</c>).</summary>
    Devolucao,
}

/// <summary>
/// Linha do LEDGER de estoque: append-only, nunca alterada nem apagada (RN-22).
/// Corrigir um erro = registrar um movimento compensatório. O ledger explica
/// CADA unidade que entrou ou saiu, com quem, quando e de quais lotes.
/// </summary>
public sealed class StockMovement : Entity<Guid>, ITenantOwned
{
    private readonly List<StockMovementLot> _lots = [];

    private StockMovement()
        : base(Guid.Empty)
    {
        ProductName = string.Empty;
        Note = string.Empty;
        By = string.Empty;
    }

    private StockMovement(TenantId tenantId, ProductId productId, string productName, StockChange change, string note, string by, Guid? orderId, string? idempotencyKey, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        ProductId = productId;
        ProductName = productName;
        Type = change.Type;
        Delta = change.Delta;
        ResultingStock = change.ResultingOnHand;
        Note = note;
        By = by;
        OrderId = orderId;
        IdempotencyKey = idempotencyKey;
        CreatedAt = now;
        _lots.AddRange(change.Allocations.Select(a => new StockMovementLot(a.LotId, a.Quantity, a.UnitCostCents)));
    }

    public TenantId TenantId { get; private set; }

    public ProductId ProductId { get; private set; }

    /// <summary>Snapshot do nome: o histórico continua legível mesmo se o produto for renomeado ou apagado.</summary>
    public string ProductName { get; private set; }

    public StockMovementType Type { get; private set; }

    public int Delta { get; private set; }

    public int ResultingStock { get; private set; }

    public string Note { get; private set; }

    public string By { get; private set; }

    public Guid? OrderId { get; private set; }

    /// <summary>Retry do mesmo evento (ex.: baixa de um pedido) não grava duas vezes: índice único.</summary>
    public string? IdempotencyKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>De quais lotes saiu (com o custo de cada um): base do custo da mercadoria vendida.</summary>
    public IReadOnlyList<StockMovementLot> Lots => _lots.AsReadOnly();

    public static StockMovement Record(TenantId tenantId, ProductId productId, string productName, StockChange change, string note, string by, DateTimeOffset now, Guid? orderId = null, string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new StockMovement(tenantId, productId, productName, change, note, by, orderId, idempotencyKey, now);
    }
}

public sealed class StockMovementLot
{
    private StockMovementLot()
    {
    }

    internal StockMovementLot(Guid lotId, int quantity, long? unitCostCents)
    {
        LotId = lotId;
        Quantity = quantity;
        UnitCostCents = unitCostCents;
    }

    public Guid LotId { get; private set; }

    public int Quantity { get; private set; }

    public long? UnitCostCents { get; private set; }
}
