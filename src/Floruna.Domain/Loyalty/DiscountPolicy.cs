namespace Floruna.Domain.Loyalty;

/// <summary>Cupom e desconto de nível: vale o maior (<c>best_of</c>, padrão) ou somam (<c>cumulative</c>).</summary>
public enum StackingMode
{
    BestOf,
    Cumulative,
}

/// <summary>Por que um benefício não foi aplicado inteiro (a vitrine explica isso ao cliente).</summary>
public enum PolicyNote
{
    CouponBeatsTier,
    TierBeatsCoupon,
    DiscountCapped,
    CashbackBlockedByCoupon,
    CashbackBelowMinimum,
    CashbackCapped,
}

public sealed record DiscountRules(
    StackingMode Stacking, int MaxTotalDiscountPercent, int MaxCashbackPercentOfOrder, bool AllowCashbackWithCoupon, long CashbackMinRedemption);

/// <param name="MerchandiseTotal">Mercadoria em centavos, com as promoções de produto já aplicadas.</param>
/// <param name="CouponDiscount">Desconto do cupom já calculado (centavos).</param>
/// <param name="RequestedCashback">Quanto do saldo o cliente PEDIU para usar.</param>
public sealed record DiscountInput(
    long MerchandiseTotal, long DeliveryFee, long GiftCardFee, long CouponDiscount, bool CouponFreeShipping,
    decimal TierDiscountPercent, bool TierFreeShipping, long RequestedCashback, long AvailableCashback);

public sealed record DiscountBreakdown(
    long TierDiscount, long CouponDiscount, long DeliveryFee, long CashbackUsed, long GrandTotal, IReadOnlyList<PolicyNote> Notes)
{
    public long MerchandiseDiscount => TierDiscount + CouponDiscount;
}

/// <summary>
/// Quanto a loja abre mão em UM pedido. Funções PURAS e determinísticas: os mesmos números
/// entram, os mesmos saem, então o checkout recalcula no servidor e a vitrine pré-visualiza sem divergir.
/// </summary>
/// <remarks>
/// Ordem FIXA de aplicação:
/// <list type="number">
/// <item>preço do item (promoções de produto já embutidas);</item>
/// <item>desconto de MERCADORIA: nível e cupom, combinados pelo modo e limitados pelo teto;</item>
/// <item>frete: zerado se o nível OU o cupom derem frete grátis;</item>
/// <item>cashback, sobre o valor já descontado. <b>Cashback não é desconto, é meio de pagamento</b>:
/// fica fora do teto de desconto e tem o seu próprio limite.</item>
/// </list>
/// </remarks>
public static class DiscountPolicy
{
    public static DiscountBreakdown Resolve(DiscountInput input, DiscountRules rules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);
        var notes = new List<PolicyNote>();

        var merchandise = Math.Max(0, input.MerchandiseTotal);
        var rawTier = Round(merchandise * ClampPercent(input.TierDiscountPercent) / 100m);
        var rawCoupon = Math.Clamp(input.CouponDiscount, 0, merchandise);
        var tier = rawTier;
        var coupon = rawCoupon;

        if (rules.Stacking == StackingMode.BestOf && rawTier > 0 && rawCoupon > 0)
        {
            // Vale o maior, nunca a soma. Empate: o cupom, porque foi escolha explícita do cliente.
            if (rawCoupon >= rawTier)
            {
                tier = 0;
                notes.Add(PolicyNote.CouponBeatsTier);
            }
            else
            {
                coupon = 0;
                notes.Add(PolicyNote.TierBeatsCoupon);
            }
        }

        // Teto duro. Corta PRIMEIRO o automático (nível): ver "cupom aplicado pela metade" é pior.
        var cap = Round(merchandise * ClampPercent(rules.MaxTotalDiscountPercent) / 100m);
        if (tier + coupon > cap)
        {
            notes.Add(PolicyNote.DiscountCapped);
            coupon = Math.Min(coupon, cap);
            tier = Math.Max(0, cap - coupon);
        }

        var freeShipping = input.CouponFreeShipping || input.TierFreeShipping;
        var deliveryFee = freeShipping ? 0 : Math.Max(0, input.DeliveryFee);
        var payable = merchandise - (tier + coupon) + Math.Max(0, input.GiftCardFee) + deliveryFee;
        var cashback = ResolveCashback(input, rules, payable, couponApplied: coupon > 0, notes);

        return new DiscountBreakdown(tier, coupon, deliveryFee, cashback, Math.Max(0, payable - cashback), notes);
    }

    /// <summary>
    /// Cashback que o pedido GERA: só sobre a mercadoria paga em dinheiro. Frete não é margem, e a
    /// parte quitada com saldo não gera saldo de novo (senão o saldo vira perpétuo).
    /// </summary>
    public static long CashbackEarned(long merchandiseTotal, long merchandiseDiscount, long cashbackUsed, decimal percent)
    {
        var basis = Math.Max(0, merchandiseTotal - merchandiseDiscount - cashbackUsed);
        return Round(basis * ClampPercent(percent) / 100m);
    }

    private static long ResolveCashback(DiscountInput input, DiscountRules rules, long payable, bool couponApplied, List<PolicyNote> notes)
    {
        var requested = Math.Max(0, input.RequestedCashback);
        if (requested == 0)
        {
            return 0;
        }

        if (couponApplied && !rules.AllowCashbackWithCoupon)
        {
            notes.Add(PolicyNote.CashbackBlockedByCoupon);
            return 0;
        }

        var available = Math.Max(0, input.AvailableCashback);
        if (available < rules.CashbackMinRedemption)
        {
            notes.Add(PolicyNote.CashbackBelowMinimum);
            return 0;
        }

        // Teto do pedido arredondado PARA BAIXO: nunca deixa quitar mais do que o percentual.
        var orderCap = (long)Math.Floor(payable * ClampPercent(rules.MaxCashbackPercentOfOrder) / 100m);
        var used = Math.Min(requested, Math.Min(available, orderCap));
        if (used < requested)
        {
            notes.Add(PolicyNote.CashbackCapped);
        }

        return Math.Max(0, used);
    }

    private static decimal ClampPercent(decimal value) => Math.Clamp(value, 0, 100);

    /// <summary>Meio centavo arredonda para cima (o <c>Math.round</c> do JS, para valores positivos).</summary>
    private static long Round(decimal value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
