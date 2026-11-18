using Floruna.SharedKernel;

namespace Floruna.Domain.Delivery;

/// <summary>O que vale para uma janela: aberta? com que limite? até quando aceita pedido?</summary>
/// <param name="Capacity">null = ilimitada.</param>
/// <param name="ScopeZoneId">
/// Em que escopo a ocupação é CONTADA: a zona da regra (regra por zona) ou null (regra
/// global, todas as zonas somam). Contar no escopo errado deixaria passar 10 por zona
/// num limite global de 10.
/// </param>
public sealed record SlotPolicy(bool Open, int? Capacity, DateTimeOffset AcceptsUntil, Guid? RuleId, Guid? ScopeZoneId);

/// <summary>
/// Calendário de entrega: função PURA que resolve a regra de capacidade que vale para
/// (data, janela, zona). Precedência, da mais forte para a mais fraca:
/// <c>blackout</c> &gt; regra da <c>data</c> &gt; regra <c>recorrente</c> do dia da semana &gt; sem regra (ilimitado).
/// Dentro do mesmo tipo, a mais específica vence (zona e janela informadas &gt; genéricas).
/// A véspera de Dia das Mães não pode depender de qual regra foi cadastrada primeiro.
/// </summary>
public static class DeliveryCalendar
{
    public static readonly IReadOnlyDictionary<DeliveryWindow, int> WindowStartHour = new Dictionary<DeliveryWindow, int>
    {
        [DeliveryWindow.Manha] = 8,
        [DeliveryWindow.Tarde] = 13,
        [DeliveryWindow.Noite] = 18,
    };

    public static readonly Error ClosedDate = Error.Conflict("CONFLICT", "A loja não entrega nesta data. Escolha outra data de entrega.");
    public static readonly Error CutoffPassed = Error.Conflict("CONFLICT", "O prazo para esta janela de entrega já passou. Escolha outro horário.");
    public static readonly Error SlotFull = Error.Conflict("CONFLICT", "Esta janela de entrega está lotada. Escolha outro horário.");

    /// <summary>Início da janela no fuso DA LOJA (RN-50), convertido para um instante absoluto.</summary>
    public static DateTimeOffset WindowStart(DateOnly date, DeliveryWindow window, TimeZoneInfo storeZone)
    {
        ArgumentNullException.ThrowIfNull(storeZone);
        var local = date.ToDateTime(new TimeOnly(WindowStartHour[window], 0));
        return new DateTimeOffset(local, storeZone.GetUtcOffset(local));
    }

    public static SlotPolicy Resolve(IEnumerable<CapacityRule> rules, DateOnly date, DeliveryWindow window, Guid? zoneId, TimeZoneInfo storeZone)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var applicable = rules
            .Where(r => r.IsActive)
            .Where(r => r.Window is null || r.Window == window)
            .Where(r => r.ZoneId is null || r.ZoneId == zoneId)
            .Where(r => r.Kind == CapacityRuleKind.Recurring ? r.Weekday == (int)date.DayOfWeek : r.Date == date)
            .ToList();

        var start = WindowStart(date, window, storeZone);
        if (applicable.Any(r => r.Kind == CapacityRuleKind.Blackout))
        {
            return new SlotPolicy(false, 0, start, null, null);
        }

        var rule = applicable
            .OrderBy(r => r.Kind == CapacityRuleKind.Date ? 0 : 1)
            .ThenBy(r => r.ZoneId is null ? 1 : 0)
            .ThenBy(r => r.Window is null ? 1 : 0)
            .FirstOrDefault();

        return rule is null
            ? new SlotPolicy(true, null, start, null, null)
            : new SlotPolicy(true, rule.Capacity, start.AddMinutes(-rule.CutoffMinutes), rule.Id, rule.ZoneId);
    }

    /// <summary>Confere a janela para um novo pedido. <paramref name="used"/> = pedidos já aceitos nela.</summary>
    public static Result Check(SlotPolicy policy, DateTimeOffset now, int used)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Open)
        {
            return ClosedDate;
        }

        if (now >= policy.AcceptsUntil)
        {
            return CutoffPassed;
        }

        return policy.Capacity is { } capacity && used >= capacity ? SlotFull : Result.Success();
    }
}

public interface IDeliveryRepository
{
    void Add(DeliveryZone zone);

    void Add(CapacityRule rule);

    void Remove(DeliveryZone zone);

    void Remove(CapacityRule rule);

    Task<DeliveryZone?> GetZoneAsync(Guid id, CancellationToken cancellationToken);

    Task<CapacityRule?> GetRuleAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Zona ativa que cobre o CEP (a de menor taxa, se várias).</summary>
    Task<DeliveryZone?> FindZoneByZipAsync(int zip, CancellationToken cancellationToken);

    Task<bool> AnyZoneAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CapacityRule>> RulesForAsync(DateOnly date, CancellationToken cancellationToken);

    Task<int> UsedAsync(DateOnly date, DeliveryWindow window, Guid? zoneId, CancellationToken cancellationToken);

    /// <summary>
    /// Ocupa uma vaga na janela com UPSERT condicional (<c>used &lt; capacity</c>).
    /// Duas compras pela última vaga: só uma consegue.
    /// </summary>
    Task<bool> TryOccupyAsync(DateOnly date, DeliveryWindow window, Guid? zoneId, int? capacity, CancellationToken cancellationToken);

    Task FreeAsync(DateOnly date, DeliveryWindow window, Guid? zoneId, CancellationToken cancellationToken);
}
