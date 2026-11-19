using Floruna.SharedKernel;

namespace Floruna.Domain.Loyalty;

/// <summary>Nível do programa. Requisitos CONJUNTIVOS (todos precisam ser cumpridos).</summary>
public sealed record LoyaltyTier(string Key, string Name, int MinOrders, int MinFlowerKinds, long MinSpent, decimal DiscountPercent, bool FreeShipping);

public sealed record TierMissing(int Orders, int FlowerKinds, long Spent);

/// <param name="Progress">Fração 0–1 até o próximo nível, pelo requisito MAIS ATRASADO.</param>
public sealed record TierProgress(LoyaltyTier? Current, LoyaltyTier? Next, decimal Progress, TierMissing Missing);

/// <summary>Resolve o nível a partir do histórico. Função pura (mesmo algoritmo do Nest).</summary>
public static class LoyaltyTiers
{
    /// <summary>Os níveis padrão do Floruna (mesmos do Nest).</summary>
    public static readonly IReadOnlyList<LoyaltyTier> Defaults =
    [
        new("semente", "Semente", 1, 0, 0, 0, false),
        new("broto", "Broto", 3, 0, 0, 5, false),
        new("jardineiro", "Jardineiro Aprendiz", 5, 3, 50_000, 8, true),
        new("florista_vip", "Florista VIP", 8, 4, 100_000, 12, true),
        new("jardim_eterno", "Jardim Eterno", 15, 6, 300_000, 15, true),
    ];

    /// <summary>
    /// O cliente ocupa o ÚLTIMO nível (na ordem da lista) cujos requisitos cumpre
    /// inteiramente; o próximo é o PRIMEIRO que ainda não cumpre. O progresso mostrado
    /// é o do requisito mais atrasado: prometer "90%" quando falta metade do valor gasto
    /// é a forma mais rápida de perder a confiança do cliente.
    /// </summary>
    public static TierProgress Progress(IReadOnlyList<LoyaltyTier> tiers, int orders, int flowerKinds, long spent)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        var current = tiers.LastOrDefault(Meets);
        var next = tiers.FirstOrDefault(t => !Meets(t));
        if (next is null)
        {
            return new TierProgress(current, null, 1, new TierMissing(0, 0, 0));
        }

        var missing = new TierMissing(Math.Max(0, next.MinOrders - orders), Math.Max(0, next.MinFlowerKinds - flowerKinds), Math.Max(0, next.MinSpent - spent));
        var progress = Math.Min(Ratio(orders, next.MinOrders), Math.Min(Ratio(flowerKinds, next.MinFlowerKinds), Ratio(spent, next.MinSpent)));
        return new TierProgress(current, next, progress, missing);

        bool Meets(LoyaltyTier t) => orders >= t.MinOrders && flowerKinds >= t.MinFlowerKinds && spent >= t.MinSpent;
    }

    private static decimal Ratio(long value, long target) => target <= 0 ? 1 : Math.Clamp((decimal)value / target, 0, 1);
}

/// <summary>
/// Programa de fidelidade da loja (cashback + níveis). Desligado por padrão: cashback é
/// PASSIVO financeiro da loja, e ligar é uma decisão consciente (e de plano: Pro em diante).
/// </summary>
public sealed class LoyaltyProgram : Entity<Guid>, ITenantOwned
{
    public const int MaxCashbackShareAllowed = 90;

    private LoyaltyProgram()
        : base(Guid.Empty)
    {
        Tiers = [];
    }

    private LoyaltyProgram(TenantId tenantId)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        Tiers = [.. LoyaltyTiers.Defaults];
        CashbackPercent = 5;
        CashbackMinRedemption = 500;
        CashbackExpiryDays = 365;
        MaxTotalDiscountPercent = 30;
        MaxCashbackPercentOfOrder = 50;
        Stacking = StackingMode.BestOf;
    }

    public TenantId TenantId { get; private set; }

    public bool Enabled { get; private set; }

    public decimal CashbackPercent { get; private set; }

    public long CashbackMinRedemption { get; private set; }

    public int CashbackExpiryDays { get; private set; }

    public StackingMode Stacking { get; private set; }

    public int MaxTotalDiscountPercent { get; private set; }

    public int MaxCashbackPercentOfOrder { get; private set; }

    public bool AllowCashbackWithCoupon { get; private set; }

    public List<LoyaltyTier> Tiers { get; private set; }

    public static LoyaltyProgram DefaultFor(TenantId tenantId) => new(tenantId);

    public DiscountRules Rules => new(Stacking, MaxTotalDiscountPercent, MaxCashbackPercentOfOrder, AllowCashbackWithCoupon, CashbackMinRedemption);

    public Result Update(LoyaltyProgramSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.CashbackPercent is < 0 or > 50 || spec.MaxTotalDiscountPercent is < 0 or > 100 || spec.CashbackExpiryDays is < 15 or > 730)
        {
            return Error.Validation("VALIDATION_ERROR", "Parâmetros do programa fora dos limites.");
        }

        // RN-34: o cashback nunca pode quitar 100% (PIX de R$ 0,00 é cobrança inválida).
        if (spec.MaxCashbackPercentOfOrder is < 0 or > MaxCashbackShareAllowed)
        {
            return Error.Validation("VALIDATION_ERROR", $"O cashback pode quitar no máximo {MaxCashbackShareAllowed}% do pedido.");
        }

        if (spec.Tiers.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count() != spec.Tiers.Count || spec.Tiers.Any(t => t.DiscountPercent is < 0 or > 50))
        {
            return Error.Validation("VALIDATION_ERROR", "Níveis inválidos: chaves repetidas ou desconto acima de 50%.");
        }

        (Enabled, CashbackPercent, CashbackMinRedemption, CashbackExpiryDays) = (spec.Enabled, spec.CashbackPercent, spec.CashbackMinRedemption, spec.CashbackExpiryDays);
        (Stacking, MaxTotalDiscountPercent, MaxCashbackPercentOfOrder, AllowCashbackWithCoupon) = (spec.Stacking, spec.MaxTotalDiscountPercent, spec.MaxCashbackPercentOfOrder, spec.AllowCashbackWithCoupon);
        Tiers = [.. spec.Tiers];
        return Result.Success();
    }
}

public sealed record LoyaltyProgramSpec(
    bool Enabled, decimal CashbackPercent, long CashbackMinRedemption, int CashbackExpiryDays, StackingMode Stacking,
    int MaxTotalDiscountPercent, int MaxCashbackPercentOfOrder, bool AllowCashbackWithCoupon, IReadOnlyList<LoyaltyTier> Tiers);

public enum LoyaltyEntryKind
{
    Earn,
    Redeem,
    RedeemReversal,
    Expire,
    Adjust,
}

/// <summary>
/// Linha do razão de cashback (append-only, ADR 0003). Créditos são LOTES com saldo
/// restante e validade; débitos têm <see cref="Remaining"/> = 0.
/// </summary>
public sealed class LoyaltyEntry : Entity<Guid>, ITenantOwned
{
    private LoyaltyEntry()
        : base(Guid.Empty)
    {
        DedupeKey = string.Empty;
        Description = string.Empty;
    }

    internal LoyaltyEntry(TenantId tenantId, Guid accountId, LoyaltyEntryKind kind, long amount, long remaining, DateTimeOffset? expiresAt, string dedupeKey, string description, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        AccountId = accountId;
        Kind = kind;
        Amount = amount;
        Remaining = remaining;
        ExpiresAt = expiresAt;
        DedupeKey = dedupeKey;
        Description = description;
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public Guid AccountId { get; private set; }

    public LoyaltyEntryKind Kind { get; private set; }

    /// <summary>Positivo = crédito; negativo = débito.</summary>
    public long Amount { get; private set; }

    public long Remaining { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>Derivada do fato de origem (<c>cashback:earn:{pedido}</c>): única por loja. Retry converge.</summary>
    public string DedupeKey { get; private set; }

    public string Description { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal void Consume(long amount) => Remaining -= amount;
}

/// <summary>
/// Carteira de um comprador NA LOJA, identificado pelo e-mail normalizado (o login é opcional
/// no checkout; amarrar ao customerId faria o cliente perder saldo ao comprar sem logar).
/// RESGATAR, porém, exige sessão: devolver saldo por e-mail seria enumeração e gasto do saldo alheio.
/// </summary>
public sealed class LoyaltyAccount : AggregateRoot<Guid>, ITenantOwned
{
    private readonly List<LoyaltyEntry> _activeCredits = [];
    private readonly List<LoyaltyEntry> _newEntries = [];

    private LoyaltyAccount()
        : base(Guid.Empty)
    {
        Email = string.Empty;
        TierKey = string.Empty;
        FlowerKinds = [];
    }

    private LoyaltyAccount(TenantId tenantId, string email, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        Email = email;
        TierKey = string.Empty;
        FlowerKinds = [];
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public string Email { get; private set; }

    /// <summary>Cache do saldo, gravado junto com o razão; recalculável pelos lotes.</summary>
    public long CashbackBalance { get; private set; }

    public long TotalSpentCents { get; private set; }

    public int OrdersCount { get; private set; }

    /// <summary>Categorias diferentes já compradas (requisito de alguns níveis).</summary>
    public List<string> FlowerKinds { get; private set; }

    public string TierKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Lotes de crédito com saldo (os esgotados ficam no banco, fora da memória).</summary>
    public IReadOnlyList<LoyaltyEntry> ActiveCredits => _activeCredits.AsReadOnly();

    /// <summary>Linhas criadas nesta operação (o repositório as insere).</summary>
    public IReadOnlyList<LoyaltyEntry> NewEntries => _newEntries.AsReadOnly();

    public static LoyaltyAccount Open(TenantId tenantId, string email, DateTimeOffset now) =>
        new(tenantId, email.Trim().ToLowerInvariant(), now);

    public void Earn(long amount, DateTimeOffset expiresAt, string dedupeKey, string description, DateTimeOffset now)
    {
        if (amount <= 0)
        {
            return;
        }

        var credit = new LoyaltyEntry(TenantId, Id, LoyaltyEntryKind.Earn, amount, amount, expiresAt, dedupeKey, description, now);
        _activeCredits.Add(credit);
        _newEntries.Add(credit);
        Recalculate();
    }

    /// <summary>Consome os lotes que vencem PRIMEIRO: o cliente nunca perde saldo que poderia ter usado.</summary>
    public Result Redeem(long amount, string dedupeKey, string description, DateTimeOffset now)
    {
        if (amount <= 0)
        {
            return Result.Success();
        }

        var usable = _activeCredits.Where(c => c.Remaining > 0 && (c.ExpiresAt is null || c.ExpiresAt > now))
            .OrderBy(c => c.ExpiresAt is null ? 1 : 0).ThenBy(c => c.ExpiresAt).ThenBy(c => c.CreatedAt).ToList();
        if (usable.Sum(c => c.Remaining) < amount)
        {
            return LoyaltyErrors.InsufficientBalance;
        }

        var left = amount;
        foreach (var credit in usable)
        {
            var take = Math.Min(left, credit.Remaining);
            credit.Consume(take);
            left -= take;
            if (left == 0)
            {
                break;
            }
        }

        _newEntries.Add(new LoyaltyEntry(TenantId, Id, LoyaltyEntryKind.Redeem, -amount, 0, null, dedupeKey, description, now));
        Recalculate();
        return Result.Success();
    }

    /// <summary>Pedido cancelado: o saldo usado volta como um lote novo.</summary>
    public void ReverseRedemption(long amount, DateTimeOffset expiresAt, string dedupeKey, DateTimeOffset now)
    {
        var credit = new LoyaltyEntry(TenantId, Id, LoyaltyEntryKind.RedeemReversal, amount, amount, expiresAt, dedupeKey, "Saldo devolvido (pedido cancelado)", now);
        _activeCredits.Add(credit);
        _newEntries.Add(credit);
        Recalculate();
    }

    /// <summary>Zera os lotes vencidos, registrando a expiração no razão.</summary>
    public long ExpireCredits(DateTimeOffset now)
    {
        long expired = 0;
        foreach (var credit in _activeCredits.Where(c => c.Remaining > 0 && c.ExpiresAt <= now).ToList())
        {
            expired += credit.Remaining;
            _newEntries.Add(new LoyaltyEntry(TenantId, Id, LoyaltyEntryKind.Expire, -credit.Remaining, 0, null, $"cashback:expire:{credit.Id}", "Saldo vencido", now));
            credit.Consume(credit.Remaining);
        }

        Recalculate();
        return expired;
    }

    /// <summary>Ajuste manual da loja (cortesia ou correção), sempre com motivo.</summary>
    public Result Adjust(long amount, int expiryDays, string reason, string dedupeKey, DateTimeOffset now)
    {
        if (amount > 0)
        {
            var credit = new LoyaltyEntry(TenantId, Id, LoyaltyEntryKind.Adjust, amount, amount, now.AddDays(expiryDays), dedupeKey, reason, now);
            _activeCredits.Add(credit);
            _newEntries.Add(credit);
            Recalculate();
            return Result.Success();
        }

        return Redeem(-amount, dedupeKey, reason, now);
    }

    /// <summary>Compra entregue: atualiza histórico e nível.</summary>
    public void RecordPurchase(long spentCents, IEnumerable<string> categories, IReadOnlyList<LoyaltyTier> tiers)
    {
        OrdersCount++;
        TotalSpentCents += spentCents;
        FlowerKinds = [.. FlowerKinds.Union(categories, StringComparer.Ordinal)];
        TierKey = LoyaltyTiers.Progress(tiers, OrdersCount, FlowerKinds.Count, TotalSpentCents).Current?.Key ?? string.Empty;
    }

    /// <summary>Saldo que pode ser usado AGORA (lotes vencidos não contam, mesmo antes do job de expiração).</summary>
    public long AvailableCashback(DateTimeOffset now) =>
        _activeCredits.Where(c => c.Remaining > 0 && (c.ExpiresAt is null || c.ExpiresAt > now)).Sum(c => c.Remaining);

    public LoyaltyTier? TierIn(IReadOnlyList<LoyaltyTier> tiers) => tiers.FirstOrDefault(t => t.Key == TierKey);

    private void Recalculate() => CashbackBalance = _activeCredits.Sum(c => c.Remaining);
}

/// <summary>Chaves de deduplicação derivadas do FATO de origem: retry converge para a mesma linha.</summary>
public static class LoyaltyKeys
{
    public static string Earn(Guid orderId) => $"cashback:earn:{orderId}";

    public static string Redeem(Guid orderId) => $"cashback:redeem:{orderId}";

    public static string Reverse(Guid orderId) => $"cashback:reverse:{orderId}";
}

public static class LoyaltyErrors
{
    public static readonly Error InsufficientBalance = Error.Conflict("CONFLICT", "Saldo de cashback insuficiente.");
    public static readonly Error AccountNotFound = Error.NotFound("NOT_FOUND", "Conta de fidelidade não encontrada.");
}

public interface ILoyaltyRepository
{
    Task<LoyaltyProgram?> GetProgramAsync(CancellationToken cancellationToken);

    void Add(LoyaltyProgram program);

    void Add(LoyaltyAccount account);

    /// <summary>Conta pelo e-mail, travando a linha (resgate e acúmulo concorrentes não se atropelam).</summary>
    Task<LoyaltyAccount?> GetForUpdateAsync(string email, CancellationToken cancellationToken);

    Task<LoyaltyAccount?> GetByIdForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<LoyaltyAccount?> FindAsync(string email, CancellationToken cancellationToken);

    Task<bool> EntryExistsAsync(string dedupeKey, CancellationToken cancellationToken);

    /// <summary>Insere as linhas novas do razão (append-only: nunca atualiza nem apaga as antigas, só o saldo dos lotes).</summary>
    void Track(LoyaltyAccount account);
}
