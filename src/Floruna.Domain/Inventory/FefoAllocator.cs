namespace Floruna.Domain.Inventory;

/// <summary>Por que a saída está acontecendo. Muda quais lotes podem sair (RN-26).</summary>
public enum WithdrawalPurpose
{
    /// <summary>Venda: lote vencido NÃO pode ser vendido.</summary>
    Sale,

    /// <summary>Perda/descarte e ajuste negativo: o vencido sai PRIMEIRO (é justamente a flor murcha).</summary>
    Discard,
}

public sealed record LotAllocation(Guid LotId, int Quantity, long? UnitCostCents);

/// <param name="Shortfall">Quanto faltou. Maior que zero = não há saldo elegível; nenhuma alocação é devolvida.</param>
public sealed record FefoPlan(IReadOnlyList<LotAllocation> Allocations, int Shortfall)
{
    public bool IsSatisfied => Shortfall == 0;
}

/// <summary>
/// FEFO (First Expired, First Out): o lote que vence antes sai antes.
/// Domain service PURO, sem banco: o coração da regra de estoque perecível,
/// testável em milissegundos (RN-21).
/// </summary>
/// <remarks>
/// Ordem: lotes com validade por vencimento (desempate pelo mais antigo);
/// lotes sem validade por último, do mais antigo ao mais novo.
/// Tudo ou nada: se não houver saldo elegível suficiente, não aloca nada.
/// </remarks>
public static class FefoAllocator
{
    public static FefoPlan Plan(IEnumerable<StockLot> lots, int quantity, WithdrawalPurpose purpose, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        var eligible = lots
            .Where(l => l.Remaining > 0)
            .Where(l => purpose == WithdrawalPurpose.Discard || !l.IsExpiredAt(now))
            .OrderBy(l => l.ExpiresAt is null ? 1 : 0)
            .ThenBy(l => l.ExpiresAt)
            .ThenBy(l => l.ReceivedAt);

        var allocations = new List<LotAllocation>();
        var remaining = quantity;
        foreach (var lot in eligible)
        {
            var take = Math.Min(remaining, lot.Remaining);
            allocations.Add(new LotAllocation(lot.Id, take, lot.UnitCostCents));
            remaining -= take;
            if (remaining == 0)
            {
                return new FefoPlan(allocations, 0);
            }
        }

        return new FefoPlan([], remaining);
    }
}
