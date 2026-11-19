using Floruna.Domain.Pos;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Pos;

public sealed class CashSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private static CashSession Open(long floatCents = 10_000) =>
        CashSession.Open(TenantId.New(), Guid.NewGuid(), "Carla", floatCents, Now).Value;

    [Fact]
    public void Expected_cash_is_float_plus_cash_sales_plus_topups_minus_withdrawals()
    {
        var session = Open(10_000);
        session.AddMovement(CashMovementType.Suprimento, 5_000, "Troco extra", "Carla", cashSales: 0, Now).IsSuccess.ShouldBeTrue();
        session.AddMovement(CashMovementType.Sangria, 8_000, "Cofre", "Carla", cashSales: 20_000, Now).IsSuccess.ShouldBeTrue();

        session.ExpectedCash(cashSales: 20_000).ShouldBe(10_000 + 20_000 + 5_000 - 8_000);
    }

    [Fact]
    public void Withdrawal_cannot_take_more_than_is_in_the_drawer()
    {
        var session = Open(10_000);

        session.AddMovement(CashMovementType.Sangria, 10_001, "Cofre", "Carla", cashSales: 0, Now).IsFailure.ShouldBeTrue();
        session.AddMovement(CashMovementType.Sangria, 10_000, "Cofre", "Carla", cashSales: 0, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Closing_freezes_expected_counted_and_difference()
    {
        var session = Open(10_000);

        session.Close(countedCash: 29_500, cashSales: 20_000, Now.AddHours(8)).IsSuccess.ShouldBeTrue();

        session.Status.ShouldBe(CashSessionStatus.Closed);
        session.ExpectedCashAtClose.ShouldBe(30_000);
        session.CountedCash.ShouldBe(29_500);
        session.Difference.ShouldBe(-500); // quebra de caixa: faltou R$ 5,00
    }

    [Fact]
    public void Closed_session_accepts_nothing_else()
    {
        var session = Open();
        session.Close(10_000, 0, Now);

        session.AddMovement(CashMovementType.Suprimento, 100, "", "Carla", 0, Now).Error.ShouldBe(CashErrors.NoOpenSession);
        session.Close(10_000, 0, Now).Error.ShouldBe(CashErrors.NoOpenSession);
    }

    [Fact]
    public void Opening_float_cannot_be_negative()
    {
        CashSession.Open(TenantId.New(), Guid.NewGuid(), "Carla", -1, Now).IsFailure.ShouldBeTrue();
    }
}
