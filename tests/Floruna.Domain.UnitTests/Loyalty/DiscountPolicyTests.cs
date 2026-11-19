using Floruna.Domain.Loyalty;

namespace Floruna.Domain.UnitTests.Loyalty;

/// <summary>
/// Empilhamento de descontos (ADR 0003 do Nest). Os casos são os MESMOS do teste Jest
/// original: viram especificação executável da regra que, errada, vende abaixo do custo em silêncio.
/// </summary>
public sealed class DiscountPolicyTests
{
    private static readonly DiscountRules Rules = new(StackingMode.BestOf, MaxTotalDiscountPercent: 30, MaxCashbackPercentOfOrder: 50,
        AllowCashbackWithCoupon: true, CashbackMinRedemption: 1_000);

    private static readonly DiscountInput Base = new(MerchandiseTotal: 20_000, DeliveryFee: 1_500, GiftCardFee: 0, CouponDiscount: 0,
        CouponFreeShipping: false, TierDiscountPercent: 0, TierFreeShipping: false, RequestedCashback: 0, AvailableCashback: 0);

    [Fact]
    public void Without_benefits_charges_merchandise_plus_delivery()
    {
        var result = DiscountPolicy.Resolve(Base, Rules);

        result.GrandTotal.ShouldBe(21_500);
        result.Notes.ShouldBeEmpty();
    }

    [Fact]
    public void Best_of_gives_the_bigger_discount_never_the_sum()
    {
        var result = DiscountPolicy.Resolve(Base with { TierDiscountPercent = 15, CouponDiscount = 2_000 }, Rules);

        result.TierDiscount.ShouldBe(3_000);
        result.CouponDiscount.ShouldBe(0);
        result.MerchandiseDiscount.ShouldBe(3_000);
        result.Notes.ShouldContain(PolicyNote.TierBeatsCoupon);
    }

    [Fact]
    public void On_a_tie_the_coupon_the_customer_chose_wins()
    {
        var result = DiscountPolicy.Resolve(Base with { TierDiscountPercent = 10, CouponDiscount = 2_000 }, Rules);

        result.CouponDiscount.ShouldBe(2_000);
        result.TierDiscount.ShouldBe(0);
        result.Notes.ShouldContain(PolicyNote.CouponBeatsTier);
    }

    [Fact]
    public void Cumulative_adds_both_but_respects_the_cap_cutting_the_tier_first()
    {
        var result = DiscountPolicy.Resolve(Base with { TierDiscountPercent = 20, CouponDiscount = 4_000 }, Rules with { Stacking = StackingMode.Cumulative });

        result.MerchandiseDiscount.ShouldBe(6_000); // 30% de 200,00
        result.CouponDiscount.ShouldBe(4_000);      // o cupom escolhido fica inteiro
        result.TierDiscount.ShouldBe(2_000);        // o automático é cortado
        result.Notes.ShouldContain(PolicyNote.DiscountCapped);
    }

    [Fact]
    public void Cap_cuts_even_when_only_the_coupon_exceeds_it()
    {
        var result = DiscountPolicy.Resolve(Base with { CouponDiscount = 12_000 }, Rules);

        result.MerchandiseDiscount.ShouldBe(6_000);
        result.GrandTotal.ShouldBe(15_500);
    }

    [Fact]
    public void Tier_free_shipping_zeroes_the_delivery()
    {
        var result = DiscountPolicy.Resolve(Base with { TierFreeShipping = true }, Rules);

        result.DeliveryFee.ShouldBe(0);
        result.GrandTotal.ShouldBe(20_000);
    }

    [Fact]
    public void Cashback_is_limited_to_a_share_of_the_order()
    {
        var result = DiscountPolicy.Resolve(Base with { RequestedCashback = 20_000, AvailableCashback = 20_000 }, Rules);

        result.CashbackUsed.ShouldBe(10_750); // 50% de 215,00
        result.GrandTotal.ShouldBe(10_750);
        result.Notes.ShouldContain(PolicyNote.CashbackCapped);
    }

    [Fact]
    public void Store_may_let_the_balance_cover_everything()
    {
        var result = DiscountPolicy.Resolve(Base with { RequestedCashback = 1_000_000, AvailableCashback = 1_000_000 }, Rules with { MaxCashbackPercentOfOrder = 100 });

        result.CashbackUsed.ShouldBe(21_500);
        result.GrandTotal.ShouldBe(0);
    }

    [Fact]
    public void Balance_below_the_minimum_is_not_redeemable()
    {
        var result = DiscountPolicy.Resolve(Base with { RequestedCashback = 500, AvailableCashback = 500 }, Rules);

        result.CashbackUsed.ShouldBe(0);
        result.Notes.ShouldContain(PolicyNote.CashbackBelowMinimum);
    }

    [Fact]
    public void Cashback_is_blocked_with_a_coupon_when_the_store_disallows()
    {
        var result = DiscountPolicy.Resolve(Base with { CouponDiscount = 2_000, RequestedCashback = 5_000, AvailableCashback = 5_000 },
            Rules with { AllowCashbackWithCoupon = false });

        result.CashbackUsed.ShouldBe(0);
        result.Notes.ShouldContain(PolicyNote.CashbackBlockedByCoupon);
    }

    [Fact]
    public void Cashback_stays_allowed_if_the_coupon_lost_to_the_tier()
    {
        var result = DiscountPolicy.Resolve(Base with { TierDiscountPercent = 15, CouponDiscount = 1_000, RequestedCashback = 2_000, AvailableCashback = 5_000 },
            Rules with { AllowCashbackWithCoupon = false });

        result.CouponDiscount.ShouldBe(0);
        result.CashbackUsed.ShouldBe(2_000);
    }

    [Fact]
    public void Absurd_inputs_are_clamped_instead_of_producing_negative_totals()
    {
        var result = DiscountPolicy.Resolve(Base with { MerchandiseTotal = -5_000, TierDiscountPercent = 999, CouponDiscount = -100, DeliveryFee = 0 }, Rules);

        result.GrandTotal.ShouldBe(0);
        result.MerchandiseDiscount.ShouldBe(0);
    }

    [Fact]
    public void Earned_cashback_only_counts_what_was_paid_in_money()
    {
        DiscountPolicy.CashbackEarned(20_000, 3_000, 5_000, 5).ShouldBe(600);
        DiscountPolicy.CashbackEarned(20_000, 0, 20_000, 5).ShouldBe(0); // pago todo com saldo: não gera saldo de novo
    }
}
