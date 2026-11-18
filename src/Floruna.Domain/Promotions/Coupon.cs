using Floruna.SharedKernel;

namespace Floruna.Domain.Promotions;

/// <summary>Valores de contrato: <c>percent</c>, <c>fixed</c>, <c>free_shipping</c>.</summary>
public enum CouponType
{
    Percent,
    Fixed,
    FreeShipping,
}

/// <summary>O que o cupom dá num pedido.</summary>
public sealed record CouponBenefit(long DiscountCents, bool FreeShipping);

/// <summary>
/// Cupom de desconto (RN-30, RN-31). O cálculo do desconto é uma STRATEGY por tipo
/// (switch fechado sobre um enum: adicionar um tipo novo obriga a tratar aqui).
/// </summary>
public sealed class Coupon : AggregateRoot<Guid>, ITenantOwned
{
    private Coupon()
        : base(Guid.Empty)
    {
        Code = string.Empty;
        TargetEmail = string.Empty;
    }

    private Coupon(TenantId tenantId)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        Code = string.Empty;
        TargetEmail = string.Empty;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>Sempre em maiúsculas; único por loja.</summary>
    public string Code { get; private set; }

    public CouponType Type { get; private set; }

    /// <summary>Percent: 1–100. Fixed: centavos. FreeShipping: ignorado.</summary>
    public long Value { get; private set; }

    public long MinOrderCents { get; private set; }

    public int? MaxUses { get; private set; }

    public int? PerCustomerLimit { get; private set; }

    /// <summary>Contador atualizado só por UPDATE condicional (<c>used_count &lt; max_uses</c>) no checkout.</summary>
    public int UsedCount { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>Cupom pessoal: só vale para este e-mail (vazio = qualquer um).</summary>
    public string TargetEmail { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Result<Coupon> Create(TenantId tenantId, CouponSpec spec, DateTimeOffset now)
    {
        var coupon = new Coupon(tenantId) { CreatedAt = now };
        var applied = coupon.Update(spec);
        return applied.IsSuccess ? coupon : applied.Error;
    }

    public Result Update(CouponSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var code = spec.Code.Trim().ToUpperInvariant();
        if (code.Length is < 3 or > 40)
        {
            return CouponErrors.CodeInvalid;
        }

        if (spec.Type == CouponType.Percent && spec.Value is < 1 or > 100)
        {
            return CouponErrors.PercentOutOfRange;
        }

        if (spec.Type == CouponType.Fixed && spec.Value < 1)
        {
            return Error.Validation("VALIDATION_ERROR", "O desconto fixo deve ser de ao menos 1 centavo.");
        }

        Code = code;
        Type = spec.Type;
        Value = spec.Type == CouponType.FreeShipping ? 0 : spec.Value;
        MinOrderCents = spec.MinOrderCents;
        MaxUses = spec.MaxUses;
        PerCustomerLimit = spec.PerCustomerLimit;
        ExpiresAt = spec.ExpiresAt;
        TargetEmail = spec.TargetEmail?.Trim().ToLowerInvariant() ?? string.Empty;
        IsActive = spec.IsActive;
        return Result.Success();
    }

    /// <summary>
    /// Confere se o cupom vale para este pedido (sem consumir). O limite total é
    /// conferido aqui como pré-checagem; a garantia é o UPDATE condicional na transação.
    /// </summary>
    public Result<CouponBenefit> Evaluate(long subtotalCents, string? buyerEmail, int buyerUses, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return CouponErrors.Invalid;
        }

        if (ExpiresAt is { } expires && expires < now)
        {
            return CouponErrors.Expired;
        }

        if (MaxUses is { } max && UsedCount >= max)
        {
            return CouponErrors.Exhausted;
        }

        if (subtotalCents < MinOrderCents)
        {
            return CouponErrors.MinOrderNotMet(MinOrderCents);
        }

        var buyer = buyerEmail?.Trim().ToLowerInvariant() ?? string.Empty;

        // Mesma resposta de "cupom inexistente": não confirmar que o código existe para outra pessoa.
        if (TargetEmail.Length > 0 && TargetEmail != buyer)
        {
            return CouponErrors.Invalid;
        }

        if (PerCustomerLimit is { } limit)
        {
            if (buyer.Length == 0)
            {
                return CouponErrors.EmailRequired;
            }

            if (buyerUses >= limit)
            {
                return CouponErrors.AlreadyUsed;
            }
        }

        var subtotal = Money.FromCents(subtotalCents);
        return Type switch
        {
            CouponType.Percent => new CouponBenefit(subtotal.Percent(Value).Cents, false),
            CouponType.Fixed => new CouponBenefit(Math.Min(Value, subtotalCents), false), // RN-35: nunca maior que os itens
            CouponType.FreeShipping => new CouponBenefit(0, true),
            _ => throw new InvalidOperationException($"Tipo de cupom sem regra: {Type}"),
        };
    }
}

public sealed record CouponSpec(
    string Code, CouponType Type, long Value, long MinOrderCents, int? MaxUses, int? PerCustomerLimit,
    DateTimeOffset? ExpiresAt, string? TargetEmail, bool IsActive);

/// <summary>Uso de um cupom num pedido. Sustenta o limite por cliente e a medição de receita.</summary>
public sealed class CouponRedemption : Entity<Guid>, ITenantOwned
{
    private CouponRedemption()
        : base(Guid.Empty)
    {
        Code = string.Empty;
        Email = string.Empty;
    }

    public CouponRedemption(TenantId tenantId, Guid couponId, string code, string? email, Guid orderId, long discountCents, long orderTotalCents, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        CouponId = couponId;
        Code = code;
        Email = email?.Trim().ToLowerInvariant() ?? string.Empty;
        OrderId = orderId;
        DiscountCents = discountCents;
        OrderTotalCents = orderTotalCents;
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public Guid CouponId { get; private set; }

    public string Code { get; private set; }

    public string Email { get; private set; }

    public Guid OrderId { get; private set; }

    public long DiscountCents { get; private set; }

    public long OrderTotalCents { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Códigos de erro do contrato do Nest (módulo-piloto de códigos semânticos).</summary>
public static class CouponErrors
{
    public static readonly Error Invalid = Error.Validation("COUPON_INVALID", "Cupom inválido ou inativo.");
    public static readonly Error Expired = Error.Validation("COUPON_EXPIRED", "Cupom expirado.");
    public static readonly Error Exhausted = Error.Validation("COUPON_EXHAUSTED", "Cupom esgotado.");
    public static readonly Error ExhaustedDuringCheckout = Error.Conflict("COUPON_EXHAUSTED", "O cupom se esgotou enquanto o pedido era finalizado.");
    public static readonly Error EmailRequired = Error.Validation("COUPON_EMAIL_REQUIRED", "Informe o e-mail para usar este cupom.");
    public static readonly Error AlreadyUsed = Error.Validation("COUPON_ALREADY_USED", "Você já usou este cupom.");
    public static readonly Error PercentOutOfRange = Error.Validation("COUPON_PERCENT_OUT_OF_RANGE", "O percentual deve estar entre 1 e 100.");
    public static readonly Error CodeInvalid = Error.Validation("VALIDATION_ERROR", "O código deve ter entre 3 e 40 caracteres.");
    public static readonly Error CodeTaken = Error.Conflict("COUPON_CODE_TAKEN", "Já existe um cupom com este código.");
    public static readonly Error NotFound = Error.NotFound("COUPON_NOT_FOUND", "Cupom não encontrado.");

    public static Error MinOrderNotMet(long minOrderCents) => Error.Validation(
        "COUPON_MIN_ORDER_NOT_MET",
        $"Pedido mínimo para este cupom: R$ {minOrderCents / 100m:0.00}.".Replace('.', ','));
}

public interface ICouponRepository
{
    void Add(Coupon coupon);

    void Add(CouponRedemption redemption);

    void Remove(Coupon coupon);

    Task<Coupon?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Coupon?> FindActiveByCodeAsync(string code, CancellationToken cancellationToken);

    Task<int> CountRedemptionsAsync(Guid couponId, string email, CancellationToken cancellationToken);

    /// <summary>
    /// Consome um uso com UPDATE condicional (ativo, não vencido, abaixo do limite).
    /// Duas compras disputando o último uso: só uma consegue (RN-31).
    /// </summary>
    Task<bool> TryClaimUseAsync(Guid couponId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Devolve o uso de um pedido cancelado antes de ser confirmado (e apaga o resgate).</summary>
    Task ReleaseUseAsync(Guid couponId, Guid orderId, CancellationToken cancellationToken);
}
