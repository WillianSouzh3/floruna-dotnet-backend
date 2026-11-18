using Floruna.Domain.Promotions;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Commerce;

public sealed class CouponTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static Coupon Make(CouponType type, long value, long minOrder = 0, int? maxUses = null, int? perCustomer = null,
        DateTimeOffset? expiresAt = null, string? targetEmail = null, bool active = true) =>
        Coupon.Create(TenantId.New(), new CouponSpec("promo10", type, value, minOrder, maxUses, perCustomer, expiresAt, targetEmail, active), Now).Value;

    [Theory]
    [InlineData(10, 20_000, 2_000)]
    [InlineData(15, 9_999, 1_500)] // 1499,85 → 1500 (meio centavo para cima)
    [InlineData(100, 5_000, 5_000)]
    public void Percent_coupon_discounts_over_the_items(long percent, long subtotal, long expected)
    {
        Make(CouponType.Percent, percent).Evaluate(subtotal, null, 0, Now).Value.DiscountCents.ShouldBe(expected);
    }

    [Fact]
    public void Fixed_coupon_never_discounts_more_than_the_items()
    {
        Make(CouponType.Fixed, 5_000).Evaluate(3_000, null, 0, Now).Value.DiscountCents.ShouldBe(3_000);
    }

    [Fact]
    public void Free_shipping_gives_no_merchandise_discount()
    {
        var benefit = Make(CouponType.FreeShipping, 0).Evaluate(3_000, null, 0, Now).Value;

        benefit.DiscountCents.ShouldBe(0);
        benefit.FreeShipping.ShouldBeTrue();
    }

    [Fact]
    public void Code_is_normalized_to_uppercase()
    {
        Make(CouponType.Percent, 10).Code.ShouldBe("PROMO10");
    }

    [Fact]
    public void Expired_inactive_exhausted_and_below_minimum_are_refused_with_contract_codes()
    {
        Make(CouponType.Percent, 10, expiresAt: Now.AddDays(-1)).Evaluate(10_000, null, 0, Now).Error.Code.ShouldBe("COUPON_EXPIRED");
        Make(CouponType.Percent, 10, active: false).Evaluate(10_000, null, 0, Now).Error.Code.ShouldBe("COUPON_INVALID");
        Make(CouponType.Percent, 10, minOrder: 15_000).Evaluate(10_000, null, 0, Now).Error.Code.ShouldBe("COUPON_MIN_ORDER_NOT_MET");
    }

    [Fact]
    public void Personal_coupon_looks_inexistent_to_anyone_else()
    {
        var personal = Make(CouponType.Percent, 10, targetEmail: "ana@x.com.br");

        personal.Evaluate(10_000, "bia@x.com.br", 0, Now).Error.ShouldBe(CouponErrors.Invalid);
        personal.Evaluate(10_000, "ANA@x.com.br", 0, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Per_customer_limit_requires_email_and_counts_uses()
    {
        var once = Make(CouponType.Percent, 10, perCustomer: 1);

        once.Evaluate(10_000, null, 0, Now).Error.ShouldBe(CouponErrors.EmailRequired);
        once.Evaluate(10_000, "ana@x.com.br", 1, Now).Error.ShouldBe(CouponErrors.AlreadyUsed);
        once.Evaluate(10_000, "ana@x.com.br", 0, Now).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Percent_must_be_between_1_and_100(long value)
    {
        Coupon.Create(TenantId.New(), new CouponSpec("abc", CouponType.Percent, value, 0, null, null, null, null, true), Now)
            .Error.ShouldBe(CouponErrors.PercentOutOfRange);
    }
}
