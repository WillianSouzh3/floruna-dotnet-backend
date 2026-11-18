using Floruna.Domain.Catalog;
using Floruna.SharedKernel;

namespace Floruna.Domain.Inventory;

/// <summary>Lote recebido: quantidade, custo e validade comercial. A unidade da perecibilidade.</summary>
public sealed class StockLot : Entity<Guid>, ITenantOwned
{
    private StockLot()
        : base(Guid.Empty)
    {
        Note = string.Empty;
        ReceivedBy = string.Empty;
    }

    internal StockLot(TenantId tenantId, ProductId productId, int quantity, DateTimeOffset? expiresAt, long? unitCostCents, string note, string receivedBy, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        ProductId = productId;
        InitialQuantity = quantity;
        Remaining = quantity;
        ExpiresAt = expiresAt;
        UnitCostCents = unitCostCents;
        Note = note;
        ReceivedBy = receivedBy;
        ReceivedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public ProductId ProductId { get; private set; }

    public int InitialQuantity { get; private set; }

    /// <summary>Ainda no lote. Invariante: 0 ≤ Remaining ≤ InitialQuantity.</summary>
    public int Remaining { get; private set; }

    /// <summary>null = não perecível (vaso, cartão).</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public long? UnitCostCents { get; private set; }

    public string Note { get; private set; }

    public string ReceivedBy { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;

    internal void Take(int quantity)
    {
        if (quantity <= 0 || quantity > Remaining)
        {
            throw new InvalidOperationException($"Lote {Id}: não é possível tirar {quantity} de {Remaining}.");
        }

        Remaining -= quantity;
    }
}

/// <summary>Resultado de uma movimentação: o que mudou e de quais lotes saiu.</summary>
public sealed record StockChange(StockMovementType Type, int Delta, int ResultingOnHand, IReadOnlyList<LotAllocation> Allocations);

/// <summary>
/// Estoque de UM produto. Raiz de agregado que contém os lotes ativos.
/// </summary>
/// <remarks>
/// <para><b>Por que os lotes ficam DENTRO do agregado?</b> Porque a invariante
/// "saldo = soma dos lotes" precisa ser forte. No Nest, <c>Product.stock</c> e os
/// lotes eram duas fontes sincronizadas no "melhor esforço" e divergiam (dívida
/// D-012). Aqui não existe saldo guardado separado dos lotes: <see cref="OnHand"/>
/// é recalculado a cada mudança, na mesma transação.</para>
/// <para>Concorrência: as operações carregam este agregado com
/// <c>SELECT ... FOR UPDATE</c>; duas saídas simultâneas do mesmo produto
/// acontecem uma depois da outra, nunca "em cima" uma da outra (RN-20).</para>
/// </remarks>
public sealed class StockItem : AggregateRoot<ProductId>, ITenantOwned
{
    private readonly List<StockLot> _lots = [];

    private StockItem()
        : base(default)
    {
    }

    private StockItem(ProductId productId, TenantId tenantId)
        : base(productId)
    {
        TenantId = tenantId;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>Unidades fisicamente na loja (soma dos lotes ativos), incluindo vencidas.</summary>
    public int OnHand { get; private set; }

    /// <summary>Unidades seguradas por reservas (pedido em andamento). Ainda estão na loja.</summary>
    public int Reserved { get; private set; }

    /// <summary>Lotes com saldo (os esgotados não são carregados).</summary>
    public IReadOnlyList<StockLot> Lots => _lots.AsReadOnly();

    public static StockItem Open(ProductId productId, TenantId tenantId) => new(productId, tenantId);

    /// <summary>O que pode ser VENDIDO agora: não vencido e não reservado.</summary>
    public int AvailableAt(DateTimeOffset now) =>
        Math.Max(0, _lots.Where(l => !l.IsExpiredAt(now)).Sum(l => l.Remaining) - Reserved);

    /// <summary>Entrada (reposição/compra): cria um lote.</summary>
    public StockChange Receive(int quantity, DateTimeOffset? expiresAt, long? unitCostCents, string note, string by, DateTimeOffset now, StockMovementType type = StockMovementType.Entrada)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        _lots.Add(new StockLot(TenantId, Id, quantity, expiresAt, unitCostCents, note, by, now));
        Recalculate();
        return new StockChange(type, quantity, OnHand, []);
    }

    /// <summary>Saída por perda/descarte: consome em FEFO, vencidos primeiro.</summary>
    public Result<StockChange> RegisterLoss(int quantity, DateTimeOffset now) =>
        Withdraw(quantity, WithdrawalPurpose.Discard, StockMovementType.Perda, InventoryErrors.LossExceedsStock, now);

    /// <summary>Ajuste manual de inventário (±). Positivo vira lote sem validade.</summary>
    public Result<StockChange> Adjust(int delta, string note, string by, DateTimeOffset now)
    {
        if (delta == 0)
        {
            return InventoryErrors.ZeroAdjustment;
        }

        return delta > 0
            ? Receive(delta, null, null, string.IsNullOrWhiteSpace(note) ? "Ajuste de inventário" : note, by, now, StockMovementType.Ajuste)
            : Withdraw(-delta, WithdrawalPurpose.Discard, StockMovementType.Ajuste, InventoryErrors.NegativeAdjustment, now);
    }

    /// <summary>
    /// Venda: só lotes não vencidos e sem tocar no que está reservado para outros.
    /// <paramref name="fromReservation"/> = a venda consome a própria reserva.
    /// </summary>
    public Result<StockChange> Sell(int quantity, DateTimeOffset now, int fromReservation = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromReservation);
        if (quantity - fromReservation > AvailableAt(now))
        {
            return InventoryErrors.InsufficientStock;
        }

        // Primeiro solta a PRÓPRIA reserva; só então tira dos lotes. Na ordem inversa, o
        // ajuste "reserva ≤ físico" do Withdraw cortaria a reserva antes de ela ser consumida.
        var reservedBefore = Reserved;
        Reserved = Math.Max(0, Reserved - fromReservation);
        var change = Withdraw(quantity, WithdrawalPurpose.Sale, StockMovementType.Venda, InventoryErrors.InsufficientStock, now);
        if (change.IsFailure)
        {
            Reserved = reservedBefore;
        }

        return change;
    }

    /// <summary>Segura unidades para um pedido em andamento (RN-24). Não tira dos lotes.</summary>
    public Result Hold(int quantity, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (quantity > AvailableAt(now))
        {
            return InventoryErrors.InsufficientStock;
        }

        Reserved += quantity;
        return Result.Success();
    }

    /// <summary>Devolve unidades seguradas (reserva liberada ou vencida).</summary>
    public void ReleaseHold(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        Reserved = Math.Max(0, Reserved - quantity);
    }

    private Result<StockChange> Withdraw(int quantity, WithdrawalPurpose purpose, StockMovementType type, Error notEnough, DateTimeOffset now)
    {
        if (quantity <= 0)
        {
            return InventoryErrors.QuantityMustBePositive;
        }

        var plan = FefoAllocator.Plan(_lots, quantity, purpose, now);
        if (!plan.IsSatisfied)
        {
            return notEnough;
        }

        foreach (var allocation in plan.Allocations)
        {
            _lots.Single(l => l.Id == allocation.LotId).Take(allocation.Quantity);
        }

        Recalculate();
        if (Reserved > OnHand)
        {
            // Descartar o que estava reservado é decisão de quem chama; o saldo físico manda.
            Reserved = OnHand;
        }

        return new StockChange(type, -quantity, OnHand, plan.Allocations);
    }

    private void Recalculate() => OnHand = _lots.Sum(l => l.Remaining);
}

public static class InventoryErrors
{
    public static readonly Error InsufficientStock = Error.Conflict("CONFLICT", "Estoque insuficiente.");
    public static readonly Error LossExceedsStock = Error.Validation(
        "VALIDATION_ERROR", "Quantidade de perda maior que o estoque disponível.", ["Quantidade de perda maior que o estoque disponível."]);
    public static readonly Error NegativeAdjustment = Error.Validation(
        "VALIDATION_ERROR", "Ajuste inválido: o estoque não pode ficar negativo.", ["Ajuste inválido: o estoque não pode ficar negativo."]);
    public static readonly Error ZeroAdjustment = Error.Validation("VALIDATION_ERROR", "O ajuste não pode ser zero.", ["O ajuste não pode ser zero."]);
    public static readonly Error QuantityMustBePositive = Error.Validation("VALIDATION_ERROR", "A quantidade deve ser maior que zero.");
    public static readonly Error ReservationNotFound = Error.NotFound("NOT_FOUND", "Reserva não encontrada.");
    public static readonly Error ReservationClosed = Error.NotFound("NOT_FOUND", "Reserva já encerrada.");
}
