using System.Globalization;
using System.Text.RegularExpressions;
using Floruna.SharedKernel;

namespace Floruna.Domain.Delivery;

/// <summary>Janelas de entrega (contrato: <c>manha</c>, <c>tarde</c>, <c>noite</c>).</summary>
public enum DeliveryWindow
{
    Manha,
    Tarde,
    Noite,
}

public enum DeliveryMethod
{
    Entrega,
    Retirada,
}

public static partial class Cep
{
    /// <summary>"01310-100" → 1310100. Null se inválido.</summary>
    public static int? ToNumber(string? cep)
    {
        var digits = NonDigits().Replace(cep ?? string.Empty, string.Empty);
        return digits.Length == 8 ? int.Parse(digits, CultureInfo.InvariantCulture) : null;
    }

    public static readonly Error Invalid = Error.Validation("VALIDATION_ERROR", "CEP inválido: use o formato 00000-000.", ["CEP inválido: use o formato 00000-000."]);

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();
}

/// <summary>Faixa de CEP com taxa de entrega. A menor taxa vence quando as faixas se sobrepõem.</summary>
public sealed class DeliveryZone : AggregateRoot<Guid>, ITenantOwned
{
    private DeliveryZone()
        : base(Guid.Empty)
    {
        Name = string.Empty;
    }

    private DeliveryZone(TenantId tenantId)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        Name = string.Empty;
    }

    public TenantId TenantId { get; private set; }

    public string Name { get; private set; }

    public int ZipStart { get; private set; }

    public int ZipEnd { get; private set; }

    public long FeeCents { get; private set; }

    public bool IsActive { get; private set; }

    public static Result<DeliveryZone> Create(TenantId tenantId, string name, string zipStart, string zipEnd, long feeCents, bool isActive)
    {
        var zone = new DeliveryZone(tenantId);
        var applied = zone.Update(name, zipStart, zipEnd, feeCents, isActive);
        return applied.IsSuccess ? zone : applied.Error;
    }

    public Result Update(string name, string zipStart, string zipEnd, long feeCents, bool isActive)
    {
        if (Cep.ToNumber(zipStart) is not { } start || Cep.ToNumber(zipEnd) is not { } end)
        {
            return Cep.Invalid;
        }

        if (start > end)
        {
            return Error.Validation("VALIDATION_ERROR", "O CEP inicial deve ser menor ou igual ao final.");
        }

        Name = name.Trim();
        ZipStart = start;
        ZipEnd = end;
        FeeCents = Money.FromCents(feeCents).Cents;
        IsActive = isActive;
        return Result.Success();
    }
}

public enum CapacityRuleKind
{
    /// <summary>Toda semana, num dia (0 = domingo).</summary>
    Recurring,

    /// <summary>Numa data específica (ex.: véspera de Dia das Mães com capacidade extra).</summary>
    Date,

    /// <summary>Data fechada: não entrega.</summary>
    Blackout,
}

/// <summary>
/// Regra de capacidade de entrega (D-017). SEM regras, a capacidade é ilimitada:
/// a loja que nunca configurou continua funcionando, e ligar o controle é cadastrar a primeira regra.
/// </summary>
public sealed class CapacityRule : AggregateRoot<Guid>, ITenantOwned
{
    private CapacityRule()
        : base(Guid.Empty)
    {
        Note = string.Empty;
    }

    private CapacityRule(TenantId tenantId)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        Note = string.Empty;
    }

    public TenantId TenantId { get; private set; }

    public CapacityRuleKind Kind { get; private set; }

    public int? Weekday { get; private set; }

    public DateOnly? Date { get; private set; }

    /// <summary>Null = vale para todas as janelas do dia.</summary>
    public DeliveryWindow? Window { get; private set; }

    /// <summary>Null = vale para todas as zonas.</summary>
    public Guid? ZoneId { get; private set; }

    public int Capacity { get; private set; }

    /// <summary>Minutos ANTES do início da janela em que os pedidos fecham.</summary>
    public int CutoffMinutes { get; private set; }

    public bool IsActive { get; private set; }

    public string Note { get; private set; }

    public static Result<CapacityRule> Create(TenantId tenantId, CapacityRuleSpec spec)
    {
        var rule = new CapacityRule(tenantId);
        var applied = rule.Update(spec);
        return applied.IsSuccess ? rule : applied.Error;
    }

    public Result Update(CapacityRuleSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Kind == CapacityRuleKind.Recurring && spec.Weekday is not (>= 0 and <= 6))
        {
            return Error.Validation("VALIDATION_ERROR", "Informe o dia da semana da regra.");
        }

        if (spec.Kind != CapacityRuleKind.Recurring && spec.Date is null)
        {
            return Error.Validation("VALIDATION_ERROR", "Informe a data no formato AAAA-MM-DD.");
        }

        Kind = spec.Kind;
        Weekday = spec.Kind == CapacityRuleKind.Recurring ? spec.Weekday : null;
        Date = spec.Kind == CapacityRuleKind.Recurring ? null : spec.Date;
        Window = spec.Window;
        ZoneId = spec.ZoneId;
        Capacity = spec.Kind == CapacityRuleKind.Blackout ? 0 : spec.Capacity;
        CutoffMinutes = spec.CutoffMinutes;
        IsActive = spec.IsActive;
        Note = spec.Note?.Trim() ?? string.Empty;
        return Result.Success();
    }
}

public sealed record CapacityRuleSpec(
    CapacityRuleKind Kind, int? Weekday, DateOnly? Date, DeliveryWindow? Window, Guid? ZoneId, int Capacity, int CutoffMinutes, bool IsActive, string? Note);
