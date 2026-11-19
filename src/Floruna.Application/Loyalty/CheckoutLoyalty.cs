using Floruna.Application.Abstractions;
using Floruna.Domain.Loyalty;
using Floruna.Domain.Platform;

namespace Floruna.Application.Loyalty;

/// <param name="Breakdown">O que a política decidiu (descontos, frete, cashback).</param>
/// <param name="RedeemEmail">Carteira de onde sai o cashback: SEMPRE a da sessão, nunca a digitada no checkout.</param>
public sealed record LoyaltyQuote(DiscountBreakdown Breakdown, LoyaltyProgram Program, string? RedeemEmail)
{
    /// <summary>No modo "vale o maior", o nível venceu e o cupom não deu nada: não pode gastar um uso.</summary>
    public bool CouponDiscarded => Breakdown.Notes.Contains(PolicyNote.TierBeatsCoupon);
}

/// <summary>
/// Ponte pedido → fidelidade (a dependência aponta só nessa direção). Sem programa ativo,
/// devolve null e o checkout mantém exatamente o cálculo de sempre.
/// </summary>
internal sealed class CheckoutLoyalty(ILoyaltyRepository loyalty, ITenantContext tenantContext, TimeProvider clock)
{
    public async Task<LoyaltyQuote?> QuoteAsync(
        string? buyerEmail, string? sessionEmail, long merchandiseTotal, long deliveryFee, long couponDiscount, bool couponFreeShipping,
        long requestedCashback, CancellationToken cancellationToken)
    {
        if (tenantContext.Tenant is not { } tenant || !PlanCatalog.For(tenant.EffectivePlan).Allows(PlanFeature.Loyalty))
        {
            return null;
        }

        var program = await loyalty.GetProgramAsync(cancellationToken);
        if (program is not { Enabled: true })
        {
            return null;
        }

        // O NÍVEL segue o e-mail do pedido (convidado também acumula histórico, como no Nest).
        var buyer = string.IsNullOrWhiteSpace(buyerEmail) ? null : await loyalty.FindAsync(Normalize(buyerEmail), cancellationToken);
        var tier = buyer?.TierIn(program.Tiers);

        // O RESGATE exige sessão: saldo é dinheiro, e digitar o e-mail de outra pessoa não pode gastá-lo.
        var redeemEmail = string.IsNullOrWhiteSpace(sessionEmail) ? null : Normalize(sessionEmail);
        long available = 0;
        if (redeemEmail is not null && requestedCashback > 0)
        {
            var wallet = redeemEmail == buyer?.Email ? buyer : await loyalty.FindAsync(redeemEmail, cancellationToken);
            available = wallet?.AvailableCashback(clock.GetUtcNow()) ?? 0;
        }

        var breakdown = DiscountPolicy.Resolve(
            new DiscountInput(merchandiseTotal, deliveryFee, 0, couponDiscount, couponFreeShipping,
                tier?.DiscountPercent ?? 0, tier?.FreeShipping ?? false, redeemEmail is null ? 0 : requestedCashback, available),
            program.Rules);
        return new LoyaltyQuote(breakdown, program, redeemEmail);
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
