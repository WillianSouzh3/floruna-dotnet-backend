using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Delivery;
using Floruna.SharedKernel;

namespace Floruna.Application.Delivery;

public sealed record ZoneResponse(string Id, string Name, string ZipStart, string ZipEnd, long Fee, bool IsActive)
{
    public static ZoneResponse From(DeliveryZone z)
    {
        ArgumentNullException.ThrowIfNull(z);
        return new(z.Id.ToString(), z.Name, FormatCep(z.ZipStart), FormatCep(z.ZipEnd), z.FeeCents, z.IsActive);
    }

    private static string FormatCep(int value)
    {
        var digits = value.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
        return $"{digits[..5]}-{digits[5..]}";
    }
}

public sealed record ZoneList(IReadOnlyList<ZoneResponse> DeliveryZones);

public sealed record ZoneEnvelope(ZoneResponse DeliveryZone);

/// <summary><c>GET /delivery-zones/quote?cep=</c>: taxa e nome da zona (null = sem zona).</summary>
public sealed record FeeQuote(long Fee, string? ZoneName);

public sealed record RuleResponse(
    string Id, string Kind, int? Weekday, string? Date, string? Window, string? ZoneId, int Capacity, int CutoffMinutes, bool IsActive, string Note)
{
    public static RuleResponse From(CapacityRule r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.Id.ToString(), r.Kind.ToString().ToLowerInvariant(), r.Weekday,
            r.Date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            r.Window?.ToString().ToLowerInvariant(), r.ZoneId?.ToString(), r.Capacity, r.CutoffMinutes, r.IsActive, r.Note);
    }
}

public sealed record RuleList(IReadOnlyList<RuleResponse> Rules);

public sealed record RuleEnvelope(RuleResponse Rule);

/// <summary>Resultado da cotação de entrega: dá para entregar? por quanto? por que não?</summary>
public sealed record DeliveryPromiseResponse(
    string Method, string? ZipCode, string? ZoneId, string ZoneName, long Fee, string? Date, string? Window, bool Available, IReadOnlyList<string> Reasons);

public sealed record PromiseEnvelope(DeliveryPromiseResponse Promise);

public sealed record ZoneInput(string? Name, string? ZipStart, string? ZipEnd, long? Fee, bool? IsActive);

public sealed record ListZonesQuery : IQuery<ZoneList>;

public sealed record CreateZoneCommand(ZoneInput Input) : ICommand<ZoneEnvelope>;

public sealed record UpdateZoneCommand(Guid Id, ZoneInput Input) : ICommand<ZoneEnvelope>;

public sealed record DeleteZoneCommand(Guid Id) : ICommand;

public sealed record QuoteFeeQuery(string Cep) : IQuery<FeeQuote>;

public sealed record RuleInput(string? Kind, int? Weekday, string? Date, string? Window, Guid? ZoneId, int? Capacity, int? CutoffMinutes, bool? IsActive, string? Note);

public sealed record ListRulesQuery : IQuery<RuleList>;

public sealed record CreateRuleCommand(RuleInput Input) : ICommand<RuleEnvelope>;

public sealed record UpdateRuleCommand(Guid Id, RuleInput Input) : ICommand<RuleEnvelope>;

public sealed record DeleteRuleCommand(Guid Id) : ICommand;

/// <summary><c>POST /delivery-promises/quote</c>: a mesma conta que o checkout fará.</summary>
public sealed record QuotePromiseQuery(string Method, string? ZipCode, string? Date, string? Window) : IQuery<PromiseEnvelope>;

public static class DeliveryErrors
{
    public static readonly Error ZoneNotFound = Error.NotFound("NOT_FOUND", "Zona de entrega não encontrada.");
    public static readonly Error RuleNotFound = Error.NotFound("NOT_FOUND", "Regra não encontrada.");
    public static readonly Error InvalidDate = Error.Validation("VALIDATION_ERROR", "Data inválida: use o formato AAAA-MM-DD.", ["Data inválida: use o formato AAAA-MM-DD."]);
    public static readonly Error PastDate = Error.Validation("VALIDATION_ERROR", "A data de entrega já passou.");
    public static readonly Error WindowRequired = Error.Validation("VALIDATION_ERROR", "Escolha a janela de entrega.");
    public static readonly Error ZipRequired = Error.Validation("VALIDATION_ERROR", "Informe o CEP para calcular a entrega.");
    public static readonly Error NotDelivered = Error.Conflict("CONFLICT", "Não entregamos neste CEP. Escolha retirar na loja.");

    public static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    public static DeliveryWindow? ParseWindow(string? value) => value switch
    {
        "manha" => DeliveryWindow.Manha,
        "tarde" => DeliveryWindow.Tarde,
        "noite" => DeliveryWindow.Noite,
        _ => null,
    };
}

/// <summary>O que o checkout precisa saber sobre a entrega escolhida.</summary>
public sealed record DeliveryResolution(long FeeCents, Guid? ZoneId, string ZoneName, DateOnly? Date, DeliveryWindow? Window, SlotPolicy? Slot);

/// <summary>
/// Regra de entrega compartilhada entre a cotação (vitrine) e o checkout. O checkout
/// SEMPRE recalcula na hora de gravar: uma cotação antiga nunca é aceita como verdade.
/// </summary>
public sealed class DeliveryResolver(IDeliveryRepository delivery, ITenantContext tenantContext, TimeProvider clock)
{
    public async Task<Result<DeliveryResolution>> ResolveAsync(DeliveryMethod method, string? zipCode, string? date, string? window, CancellationToken cancellationToken)
    {
        DateOnly? day = null;
        DeliveryWindow? slotWindow = null;
        if (!string.IsNullOrEmpty(date))
        {
            day = DeliveryErrors.ParseDate(date);
            if (day is null)
            {
                return DeliveryErrors.InvalidDate;
            }
        }

        if (!string.IsNullOrEmpty(window))
        {
            slotWindow = DeliveryErrors.ParseWindow(window);
            if (slotWindow is null)
            {
                return DeliveryErrors.WindowRequired;
            }
        }

        if (method == DeliveryMethod.Retirada)
        {
            return new DeliveryResolution(0, null, string.Empty, day, slotWindow, null);
        }

        if (Cep.ToNumber(zipCode) is not { } zip)
        {
            return string.IsNullOrWhiteSpace(zipCode) ? DeliveryErrors.ZipRequired : Cep.Invalid;
        }

        // RN-54: com zonas cadastradas, CEP fora delas não é entregue. Loja sem nenhuma
        // zona (ainda não configurou) entrega sem taxa, para não travar a venda.
        var zone = await delivery.FindZoneByZipAsync(zip, cancellationToken);
        if (zone is null && await delivery.AnyZoneAsync(cancellationToken))
        {
            return DeliveryErrors.NotDelivered;
        }

        if (day is not { } d)
        {
            return new DeliveryResolution(zone?.FeeCents ?? 0, zone?.Id, zone?.Name ?? string.Empty, null, null, null);
        }

        if (slotWindow is not { } w)
        {
            return DeliveryErrors.WindowRequired;
        }

        var rules = await delivery.RulesForAsync(d, cancellationToken);
        var policy = DeliveryCalendar.Resolve(rules, d, w, zone?.Id, tenantContext.Tenant!.TimeZoneInfo);
        var used = policy.Capacity is null ? 0 : await delivery.UsedAsync(d, w, policy.ScopeZoneId, cancellationToken);
        var check = DeliveryCalendar.Check(policy, clock.GetUtcNow(), used);
        return check.IsFailure
            ? check.Error
            : new DeliveryResolution(zone?.FeeCents ?? 0, zone?.Id, zone?.Name ?? string.Empty, d, w, policy);
    }
}

internal sealed class DeliveryHandlers(
    ITenantContext tenantContext, IDeliveryRepository delivery, IDeliveryQueries queries, DeliveryResolver resolver, IUnitOfWork unitOfWork)
    : IQueryHandler<ListZonesQuery, ZoneList>,
      ICommandHandler<CreateZoneCommand, ZoneEnvelope>,
      ICommandHandler<UpdateZoneCommand, ZoneEnvelope>,
      ICommandHandler<DeleteZoneCommand>,
      IQueryHandler<QuoteFeeQuery, FeeQuote>,
      IQueryHandler<ListRulesQuery, RuleList>,
      ICommandHandler<CreateRuleCommand, RuleEnvelope>,
      ICommandHandler<UpdateRuleCommand, RuleEnvelope>,
      ICommandHandler<DeleteRuleCommand>,
      IQueryHandler<QuotePromiseQuery, PromiseEnvelope>
{
    public async Task<Result<ZoneList>> Handle(ListZonesQuery request, CancellationToken cancellationToken) =>
        new ZoneList(await queries.ZonesAsync(cancellationToken));

    public async Task<Result<ZoneEnvelope>> Handle(CreateZoneCommand request, CancellationToken cancellationToken)
    {
        var i = request.Input;
        var created = DeliveryZone.Create(tenantContext.RequiredTenantId, i.Name ?? string.Empty, i.ZipStart ?? string.Empty, i.ZipEnd ?? string.Empty, i.Fee ?? 0, i.IsActive ?? true);
        if (created.IsFailure)
        {
            return created.Error;
        }

        delivery.Add(created.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ZoneEnvelope(ZoneResponse.From(created.Value));
    }

    public async Task<Result<ZoneEnvelope>> Handle(UpdateZoneCommand request, CancellationToken cancellationToken)
    {
        var zone = await delivery.GetZoneAsync(request.Id, cancellationToken);
        if (zone is null)
        {
            return DeliveryErrors.ZoneNotFound;
        }

        var current = ZoneResponse.From(zone);
        var i = request.Input;
        var updated = zone.Update(i.Name ?? zone.Name, i.ZipStart ?? current.ZipStart, i.ZipEnd ?? current.ZipEnd, i.Fee ?? zone.FeeCents, i.IsActive ?? zone.IsActive);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ZoneEnvelope(ZoneResponse.From(zone));
    }

    public async Task<Result> Handle(DeleteZoneCommand request, CancellationToken cancellationToken)
    {
        var zone = await delivery.GetZoneAsync(request.Id, cancellationToken);
        if (zone is null)
        {
            return DeliveryErrors.ZoneNotFound;
        }

        delivery.Remove(zone);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<FeeQuote>> Handle(QuoteFeeQuery request, CancellationToken cancellationToken)
    {
        if (Cep.ToNumber(request.Cep) is not { } zip)
        {
            return Cep.Invalid;
        }

        var zone = await delivery.FindZoneByZipAsync(zip, cancellationToken);
        return new FeeQuote(zone?.FeeCents ?? 0, zone?.Name);
    }

    public async Task<Result<RuleList>> Handle(ListRulesQuery request, CancellationToken cancellationToken) =>
        new RuleList(await queries.RulesAsync(cancellationToken));

    public async Task<Result<RuleEnvelope>> Handle(CreateRuleCommand request, CancellationToken cancellationToken)
    {
        var spec = Spec(request.Input, null);
        if (spec.IsFailure)
        {
            return spec.Error;
        }

        var created = CapacityRule.Create(tenantContext.RequiredTenantId, spec.Value);
        if (created.IsFailure)
        {
            return created.Error;
        }

        delivery.Add(created.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new RuleEnvelope(RuleResponse.From(created.Value));
    }

    public async Task<Result<RuleEnvelope>> Handle(UpdateRuleCommand request, CancellationToken cancellationToken)
    {
        var rule = await delivery.GetRuleAsync(request.Id, cancellationToken);
        if (rule is null)
        {
            return DeliveryErrors.RuleNotFound;
        }

        var spec = Spec(request.Input, rule);
        if (spec.IsFailure)
        {
            return spec.Error;
        }

        var updated = rule.Update(spec.Value);
        if (updated.IsFailure)
        {
            return updated.Error;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new RuleEnvelope(RuleResponse.From(rule));
    }

    public async Task<Result> Handle(DeleteRuleCommand request, CancellationToken cancellationToken)
    {
        var rule = await delivery.GetRuleAsync(request.Id, cancellationToken);
        if (rule is null)
        {
            return DeliveryErrors.RuleNotFound;
        }

        delivery.Remove(rule);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<PromiseEnvelope>> Handle(QuotePromiseQuery request, CancellationToken cancellationToken)
    {
        var method = request.Method == "retirada" ? DeliveryMethod.Retirada : DeliveryMethod.Entrega;
        var resolved = await resolver.ResolveAsync(method, request.ZipCode, request.Date, request.Window, cancellationToken);

        // A cotação nunca "falha" para a vitrine: ela diz se está disponível e por quê.
        return new PromiseEnvelope(resolved.IsSuccess
            ? new DeliveryPromiseResponse(request.Method, request.ZipCode, resolved.Value.ZoneId?.ToString(), resolved.Value.ZoneName,
                resolved.Value.FeeCents, request.Date, request.Window, true, [])
            : new DeliveryPromiseResponse(request.Method, request.ZipCode, null, string.Empty, 0, request.Date, request.Window, false, [resolved.Error.Message]));
    }

    private static Result<CapacityRuleSpec> Spec(RuleInput i, CapacityRule? current)
    {
        CapacityRuleKind? kind = i.Kind switch
        {
            "recurring" => CapacityRuleKind.Recurring,
            "date" => CapacityRuleKind.Date,
            "blackout" => CapacityRuleKind.Blackout,
            null => current?.Kind,
            _ => null,
        };
        if (kind is null)
        {
            return Error.Validation("VALIDATION_ERROR", "Tipo de regra inválido.");
        }

        DateOnly? date = current?.Date;
        if (i.Date is not null)
        {
            date = DeliveryErrors.ParseDate(i.Date);
            if (date is null)
            {
                return DeliveryErrors.InvalidDate;
            }
        }

        var window = i.Window is null ? current?.Window : DeliveryErrors.ParseWindow(i.Window);
        if (i.Window is not null && window is null)
        {
            return DeliveryErrors.WindowRequired;
        }

        var capacity = i.Capacity ?? current?.Capacity ?? 0;
        var cutoff = i.CutoffMinutes ?? current?.CutoffMinutes ?? 0;
        if (capacity is < 0 or > 10_000 || cutoff is < 0 or > 20_160)
        {
            return Error.Validation("VALIDATION_ERROR", "Capacidade (0–10000) ou prazo de corte (0–20160 min) inválido.");
        }

        return new CapacityRuleSpec(kind.Value, i.Weekday ?? current?.Weekday, date, window, i.ZoneId ?? current?.ZoneId, capacity, cutoff,
            i.IsActive ?? current?.IsActive ?? true, i.Note ?? current?.Note);
    }
}

public interface IDeliveryQueries
{
    Task<IReadOnlyList<ZoneResponse>> ZonesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RuleResponse>> RulesAsync(CancellationToken cancellationToken);
}
