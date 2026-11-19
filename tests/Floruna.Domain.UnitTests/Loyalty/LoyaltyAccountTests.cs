using Floruna.Domain.Loyalty;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Loyalty;

public sealed class LoyaltyAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static LoyaltyAccount Account() => LoyaltyAccount.Open(TenantId.New(), " Ana@Cliente.com.br ", Now);

    [Fact]
    public void Email_is_the_wallet_identity_and_is_normalized()
    {
        Account().Email.ShouldBe("ana@cliente.com.br");
    }

    [Fact]
    public void Redemption_consumes_the_credit_that_expires_first()
    {
        var account = Account();
        account.Earn(1_000, Now.AddDays(300), "a", "", Now);
        account.Earn(1_000, Now.AddDays(30), "b", "", Now);

        account.Redeem(1_500, "r", "", Now).IsSuccess.ShouldBeTrue();

        account.CashbackBalance.ShouldBe(500);
        account.ActiveCredits.Single(c => c.DedupeKey == "b").Remaining.ShouldBe(0);   // venceria antes: usado primeiro
        account.ActiveCredits.Single(c => c.DedupeKey == "a").Remaining.ShouldBe(500);
    }

    [Fact]
    public void Expired_credit_cannot_be_redeemed_and_is_expired_into_the_ledger()
    {
        var account = Account();
        account.Earn(800, Now.AddDays(-1), "old", "", Now.AddDays(-60));

        account.Redeem(500, "r", "", Now).Error.ShouldBe(LoyaltyErrors.InsufficientBalance);
        account.ExpireCredits(Now).ShouldBe(800);

        account.CashbackBalance.ShouldBe(0);
        account.NewEntries.Last().Kind.ShouldBe(LoyaltyEntryKind.Expire);
        account.NewEntries.Last().Amount.ShouldBe(-800);
    }

    [Fact]
    public void Reversal_returns_the_balance_as_a_new_credit()
    {
        var account = Account();
        account.Earn(1_000, Now.AddDays(30), "e", "", Now);
        account.Redeem(1_000, "r", "", Now);

        account.ReverseRedemption(1_000, Now.AddDays(30), "rev", Now);

        account.CashbackBalance.ShouldBe(1_000);
    }

    [Fact]
    public void Tier_follows_orders_categories_and_spending_conjunctively()
    {
        var account = Account();
        for (var i = 0; i < 5; i++)
        {
            account.RecordPurchase(10_000, ["buques"], LoyaltyTiers.Defaults);
        }

        account.TierKey.ShouldBe("broto"); // 5 pedidos e 500,00, mas só 1 categoria: não é "jardineiro"

        account.RecordPurchase(1_000, ["romance", "presentes"], LoyaltyTiers.Defaults);
        account.TierKey.ShouldBe("jardineiro");
    }

    [Fact]
    public void Program_never_lets_cashback_pay_the_whole_order()
    {
        var program = LoyaltyProgram.DefaultFor(TenantId.New());

        program.Update(new LoyaltyProgramSpec(true, 5, 500, 365, StackingMode.BestOf, 30, 100, false, LoyaltyTiers.Defaults))
            .IsFailure.ShouldBeTrue();
    }
}

public sealed class LoyaltyTiersTests
{
    [Fact]
    public void Nobody_has_a_tier_before_the_first_order()
    {
        var progress = LoyaltyTiers.Progress(LoyaltyTiers.Defaults, 0, 0, 0);

        progress.Current.ShouldBeNull();
        progress.Next!.Key.ShouldBe("semente");
        progress.Missing.Orders.ShouldBe(1);
    }

    [Fact]
    public void Progress_reports_the_most_delayed_requirement()
    {
        // Rumo a "jardineiro" (5 pedidos, 3 categorias, 500,00): pedidos em 100%, gasto em 20%.
        var progress = LoyaltyTiers.Progress(LoyaltyTiers.Defaults, 5, 3, 10_000);

        progress.Current!.Key.ShouldBe("broto");
        progress.Next!.Key.ShouldBe("jardineiro");
        progress.Progress.ShouldBe(0.2m);
        progress.Missing.ShouldBe(new TierMissing(0, 0, 40_000));
    }

    [Fact]
    public void Top_tier_has_no_next_and_full_progress()
    {
        var progress = LoyaltyTiers.Progress(LoyaltyTiers.Defaults, 20, 8, 500_000);

        progress.Current!.Key.ShouldBe("jardim_eterno");
        progress.Next.ShouldBeNull();
        progress.Progress.ShouldBe(1);
    }
}
