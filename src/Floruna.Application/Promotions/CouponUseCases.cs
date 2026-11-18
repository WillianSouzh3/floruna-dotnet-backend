using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Domain.Promotions;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Promotions;

public sealed record CouponResponse(
    string Id, string Code, string Type, long Value, long MinOrder, int? MaxUses, int? PerCustomerLimit, int UsedCount,
    string? ExpiresAt, string TargetEmail, bool IsActive, string CreatedAt)
{
    public static string ContractType(CouponType type) => type == CouponType.FreeShipping ? "free_shipping" : type.ToString().ToLowerInvariant();

    public static CouponType? ParseType(string? value) => value switch
    {
        "percent" => CouponType.Percent,
        "fixed" => CouponType.Fixed,
        "free_shipping" => CouponType.FreeShipping,
        _ => null,
    };

    public static CouponResponse From(Coupon c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(c.Id.ToString(), c.Code, ContractType(c.Type), c.Value, c.MinOrderCents, c.MaxUses, c.PerCustomerLimit, c.UsedCount,
            c.ExpiresAt is { } e ? CatalogMapping.Iso(e) : null, c.TargetEmail, c.IsActive, CatalogMapping.Iso(c.CreatedAt));
    }
}

public sealed record CouponList(IReadOnlyList<CouponResponse> Coupons);

public sealed record CouponEnvelope(CouponResponse Coupon);

public sealed record CouponSummary(string Code, string Type, long Value);

public sealed record CouponAdjustment(string Kind, string Target, long Amount, string Label);

/// <summary>Resposta de <c>GET /coupons/validate</c>, no formato do Nest.</summary>
public sealed record CouponValidationResponse(
    CouponSummary Coupon, string PricingVersion, IReadOnlyList<CouponAdjustment> Adjustments, long DiscountAmount, bool FreeShipping);

public sealed record CouponInput(
    string? Code, string? Type, long? Value, long? MinOrder, Optional<int?> MaxUses, Optional<int?> PerCustomerLimit,
    Optional<DateTimeOffset?> ExpiresAt, string? TargetEmail, bool? IsActive);

public sealed record ListCouponsQuery : IQuery<CouponList>;

public sealed record CreateCouponCommand(CouponInput Input) : ICommand<CouponEnvelope>;

public sealed record UpdateCouponCommand(Guid Id, CouponInput Input) : ICommand<CouponEnvelope>;

public sealed record DeleteCouponCommand(Guid Id) : ICommand;

/// <summary>Pré-visualização no carrinho: NÃO consome o cupom (RF-PRM-02).</summary>
public sealed record ValidateCouponQuery(string Code, long Subtotal, string? Email) : IQuery<CouponValidationResponse>;

internal sealed class CreateCouponValidator : AbstractValidator<CreateCouponCommand>
{
    public CreateCouponValidator()
    {
        RuleFor(x => x.Input.Code).NotEmpty().MinimumLength(3).WithMessage("O código deve ter ao menos 3 caracteres.");
        RuleFor(x => x.Input.Type).Must(t => CouponResponse.ParseType(t) is not null).WithMessage("Tipo de cupom inválido.");
        RuleFor(x => x.Input.Value).NotNull().GreaterThanOrEqualTo(0).WithMessage("Informe o valor do cupom.");
    }
}

internal sealed class CouponHandlers(
    ITenantContext tenantContext, ICouponRepository coupons, ICouponQueries queries, IUnitOfWork unitOfWork, TimeProvider clock)
    : IQueryHandler<ListCouponsQuery, CouponList>,
      ICommandHandler<CreateCouponCommand, CouponEnvelope>,
      ICommandHandler<UpdateCouponCommand, CouponEnvelope>,
      ICommandHandler<DeleteCouponCommand>,
      IQueryHandler<ValidateCouponQuery, CouponValidationResponse>
{
    public async Task<Result<CouponList>> Handle(ListCouponsQuery request, CancellationToken cancellationToken) =>
        new CouponList(await queries.ListAsync(cancellationToken));

    public async Task<Result<CouponEnvelope>> Handle(CreateCouponCommand request, CancellationToken cancellationToken)
    {
        var created = Coupon.Create(tenantContext.RequiredTenantId, Spec(request.Input, null), clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        coupons.Add(created.Value);
        return await SaveAsync(created.Value, cancellationToken);
    }

    public async Task<Result<CouponEnvelope>> Handle(UpdateCouponCommand request, CancellationToken cancellationToken)
    {
        var coupon = await coupons.GetAsync(request.Id, cancellationToken);
        if (coupon is null)
        {
            return CouponErrors.NotFound;
        }

        var updated = coupon.Update(Spec(request.Input, coupon));
        return updated.IsFailure ? updated.Error : await SaveAsync(coupon, cancellationToken);
    }

    public async Task<Result> Handle(DeleteCouponCommand request, CancellationToken cancellationToken)
    {
        var coupon = await coupons.GetAsync(request.Id, cancellationToken);
        if (coupon is null)
        {
            return CouponErrors.NotFound;
        }

        coupons.Remove(coupon);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<CouponValidationResponse>> Handle(ValidateCouponQuery request, CancellationToken cancellationToken)
    {
        if (request.Subtotal < 0)
        {
            return Error.Validation("COUPON_SUBTOTAL_INVALID", "Subtotal inválido.");
        }

        var coupon = await coupons.FindActiveByCodeAsync(request.Code, cancellationToken);
        if (coupon is null)
        {
            return CouponErrors.Invalid;
        }

        var uses = string.IsNullOrWhiteSpace(request.Email) || coupon.PerCustomerLimit is null
            ? 0
            : await coupons.CountRedemptionsAsync(coupon.Id, request.Email, cancellationToken);
        var benefit = coupon.Evaluate(request.Subtotal, request.Email, uses, clock.GetUtcNow());
        if (benefit.IsFailure)
        {
            return benefit.Error;
        }

        var adjustments = new List<CouponAdjustment>();
        if (benefit.Value.DiscountCents > 0)
        {
            adjustments.Add(new CouponAdjustment("discount", "merchandise", benefit.Value.DiscountCents, $"Cupom {coupon.Code}"));
        }

        if (benefit.Value.FreeShipping)
        {
            adjustments.Add(new CouponAdjustment("free_shipping", "shipping", 0, $"Frete grátis ({coupon.Code})"));
        }

        return new CouponValidationResponse(
            new CouponSummary(coupon.Code, CouponResponse.ContractType(coupon.Type), coupon.Value),
            "order-price/v1", adjustments, benefit.Value.DiscountCents, benefit.Value.FreeShipping);
    }

    private static CouponSpec Spec(CouponInput i, Coupon? current) => new(
        i.Code ?? current!.Code,
        CouponResponse.ParseType(i.Type) ?? current?.Type ?? CouponType.Percent,
        i.Value ?? current!.Value,
        i.MinOrder ?? current?.MinOrderCents ?? 0,
        i.MaxUses.Or(current?.MaxUses),
        i.PerCustomerLimit.Or(current?.PerCustomerLimit),
        i.ExpiresAt.Or(current?.ExpiresAt),
        i.TargetEmail ?? current?.TargetEmail,
        i.IsActive ?? current?.IsActive ?? true);

    private async Task<Result<CouponEnvelope>> SaveAsync(Coupon coupon, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return CouponErrors.CodeTaken;
        }

        return new CouponEnvelope(CouponResponse.From(coupon));
    }
}

public interface ICouponQueries
{
    Task<IReadOnlyList<CouponResponse>> ListAsync(CancellationToken cancellationToken);
}
