using Floruna.SharedKernel;

namespace Floruna.Domain.Pos;

/// <summary>Contrato: <c>sangria</c> (retirada) e <c>suprimento</c> (reforço).</summary>
public enum CashMovementType
{
    Sangria,
    Suprimento,
}

public enum CashSessionStatus
{
    Open,
    Closed,
}

public sealed record CashMovement(CashMovementType Type, long AmountCents, string Reason, DateTimeOffset At, string By);

/// <summary>
/// Turno de caixa de um operador do PDV. O valor ESPERADO na gaveta nunca é digitado: é
/// derivado (fundo + vendas em dinheiro + suprimentos − sangrias). No fechamento, o operador
/// informa o que contou e a diferença (quebra de caixa) fica registrada para sempre.
/// </summary>
public sealed class CashSession : AggregateRoot<Guid>, ITenantOwned
{
    private readonly List<CashMovement> _movements = [];

    private CashSession()
        : base(Guid.Empty)
    {
        OperatorName = string.Empty;
    }

    private CashSession(TenantId tenantId, Guid operatorId, string operatorName, long openingFloat, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        OperatorId = operatorId;
        OperatorName = operatorName;
        OpeningFloatCents = openingFloat;
        Status = CashSessionStatus.Open;
        OpenedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public Guid OperatorId { get; private set; }

    public string OperatorName { get; private set; }

    /// <summary>Fundo de troco na abertura.</summary>
    public long OpeningFloatCents { get; private set; }

    public IReadOnlyList<CashMovement> Movements => _movements.AsReadOnly();

    public CashSessionStatus Status { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public long? CountedCash { get; private set; }

    public long? ExpectedCashAtClose { get; private set; }

    /// <summary>Contado − esperado. Negativo = faltou dinheiro; positivo = sobrou.</summary>
    public long? Difference { get; private set; }

    public static Result<CashSession> Open(TenantId tenantId, Guid operatorId, string operatorName, long openingFloat, DateTimeOffset now) =>
        openingFloat < 0
            ? Error.Validation("VALIDATION_ERROR", "O fundo de troco não pode ser negativo.")
            : new CashSession(tenantId, operatorId, operatorName, openingFloat, now);

    /// <param name="cashSales">Dinheiro recebido nas vendas ligadas a esta sessão (vem das vendas, não do operador).</param>
    public long ExpectedCash(long cashSales) =>
        OpeningFloatCents + cashSales
        + _movements.Where(m => m.Type == CashMovementType.Suprimento).Sum(m => m.AmountCents)
        - _movements.Where(m => m.Type == CashMovementType.Sangria).Sum(m => m.AmountCents);

    public Result AddMovement(CashMovementType type, long amountCents, string reason, string by, long cashSales, DateTimeOffset now)
    {
        if (Status != CashSessionStatus.Open)
        {
            return CashErrors.NoOpenSession;
        }

        if (amountCents <= 0)
        {
            return Error.Validation("VALIDATION_ERROR", "Informe um valor maior que zero.");
        }

        // A gaveta não fica negativa: sangria maior que o dinheiro lá dentro é erro de digitação (ou pior).
        if (type == CashMovementType.Sangria && amountCents > ExpectedCash(cashSales))
        {
            return Error.Validation("VALIDATION_ERROR", $"A sangria é maior que o dinheiro na gaveta ({ExpectedCash(cashSales)} centavos).");
        }

        _movements.Add(new CashMovement(type, amountCents, reason.Trim(), now, by));
        return Result.Success();
    }

    public Result Close(long countedCash, long cashSales, DateTimeOffset now)
    {
        if (Status != CashSessionStatus.Open)
        {
            return CashErrors.NoOpenSession;
        }

        if (countedCash < 0)
        {
            return Error.Validation("VALIDATION_ERROR", "O valor contado não pode ser negativo.");
        }

        var expected = ExpectedCash(cashSales);
        Status = CashSessionStatus.Closed;
        ClosedAt = now;
        CountedCash = countedCash;
        ExpectedCashAtClose = expected;
        Difference = countedCash - expected;
        return Result.Success();
    }
}

public static class CashErrors
{
    public static readonly Error NoOpenSession = Error.NotFound("NOT_FOUND", "Nenhum caixa aberto.");
    public static readonly Error AlreadyOpen = Error.Conflict("CONFLICT", "Você já tem um caixa aberto.");
}

public interface ICashSessionRepository
{
    void Add(CashSession session);

    /// <summary>
    /// Caixa aberto do operador, TRAVADO. Movimento e fechamento usam <c>FOR UPDATE</c>; a venda usa
    /// <c>FOR SHARE</c> (<paramref name="forSale"/>): várias vendas andam juntas, mas o fechamento espera
    /// as vendas em andamento terminarem, e uma venda que chega depois vê o caixa já fechado.
    /// </summary>
    Task<CashSession?> GetOpenForUpdateAsync(Guid operatorId, bool forSale, CancellationToken cancellationToken);

    Task<CashSession?> FindOpenAsync(Guid operatorId, CancellationToken cancellationToken);

    /// <summary>Dinheiro recebido nas vendas da sessão (soma de <c>orders.cash_received_cents</c>).</summary>
    Task<long> CashSalesAsync(Guid sessionId, CancellationToken cancellationToken);
}
