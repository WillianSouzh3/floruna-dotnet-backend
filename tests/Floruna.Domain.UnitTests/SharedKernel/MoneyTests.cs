using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.SharedKernel;

public sealed class MoneyTests
{
    [Fact]
    public void FromCents_creates_value_in_BRL_by_default()
    {
        var money = Money.FromCents(12_990);

        money.Cents.ShouldBe(12_990);
        money.Currency.ShouldBe("BRL");
    }

    [Fact]
    public void FromCents_rejects_negative_amount()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Money.FromCents(-1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("REAL")]
    public void FromCents_rejects_invalid_currency(string currency)
    {
        Should.Throw<ArgumentException>(() => Money.FromCents(100, currency));
    }

    [Fact]
    public void Values_with_same_amount_and_currency_are_equal()
    {
        Money.FromCents(500).ShouldBe(Money.FromCents(500));
        Money.FromCents(500, "usd").ShouldBe(Money.FromCents(500, "USD"));
    }

    [Fact]
    public void Add_and_multiply_are_exact_in_cents()
    {
        // 0,10 + 0,20 = 0,30 exatamente (em double seria 0,30000000000000004)
        (Money.FromCents(10) + Money.FromCents(20)).ShouldBe(Money.FromCents(30));
        (Money.FromCents(4_590) * 3).ShouldBe(Money.FromCents(13_770));
    }

    [Fact]
    public void Subtract_that_would_go_negative_throws()
    {
        Should.Throw<InvalidOperationException>(() => Money.FromCents(100) - Money.FromCents(101));
    }

    [Fact]
    public void SubtractFloorZero_never_goes_below_zero()
    {
        Money.FromCents(100).SubtractFloorZero(Money.FromCents(150)).ShouldBe(Money.Zero());
    }

    [Fact]
    public void Operations_between_different_currencies_throw()
    {
        Should.Throw<InvalidOperationException>(() => Money.FromCents(100) + Money.FromCents(100, "USD"));
    }

    [Theory]
    [InlineData(10_000, 10, 1_000)]
    [InlineData(999, 10, 100)]   // 99,9 → 100 (meio para cima)
    [InlineData(995, 10, 100)]   // 99,5 → 100 (AwayFromZero, igual ao Math.round do JS)
    [InlineData(994, 10, 99)]    // 99,4 → 99
    [InlineData(12_345, 0, 0)]
    [InlineData(12_345, 100, 12_345)]
    public void Percent_rounds_to_nearest_cent_half_up(long cents, int percent, long expected)
    {
        Money.FromCents(cents).Percent(percent).Cents.ShouldBe(expected);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Percent_outside_0_to_100_throws(int percent)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Money.FromCents(100).Percent(percent));
    }

    [Fact]
    public void Comparison_and_Min_work_on_cents()
    {
        var small = Money.FromCents(100);
        var big = Money.FromCents(200);

        (small < big).ShouldBeTrue();
        Money.Min(big, small).ShouldBe(small);
    }

    [Fact]
    public void ToString_formats_as_currency_and_reais()
    {
        Money.FromCents(12_990).ToString().ShouldBe("BRL 129.90");
    }
}
