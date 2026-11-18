using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.SharedKernel;

public sealed class ResultTests
{
    private static readonly Error CouponExpired = Error.Conflict("COUPON_EXPIRED", "Cupom vencido.");

    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.Error.ShouldBe(Error.None);
    }

    [Fact]
    public void Failure_carries_the_error()
    {
        var result = Result.Failure(CouponExpired);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("COUPON_EXPIRED");
        result.Error.Type.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public void Failure_without_error_is_rejected()
    {
        Should.Throw<ArgumentException>(() => Result.Failure(Error.None));
    }

    [Fact]
    public void Value_and_Error_convert_implicitly()
    {
        Result<int> success = 42;
        Result<int> failure = CouponExpired;

        success.Value.ShouldBe(42);
        failure.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Reading_value_of_failure_throws()
    {
        Result<int> failure = CouponExpired;

        Should.Throw<InvalidOperationException>(() => _ = failure.Value);
    }

    [Fact]
    public void Map_transforms_success_and_propagates_failure()
    {
        Result<int> success = 21;
        Result<int> failure = CouponExpired;

        success.Map(x => x * 2).Value.ShouldBe(42);
        failure.Map(x => x * 2).Error.ShouldBe(CouponExpired);
    }

    [Fact]
    public void Validation_error_keeps_issues()
    {
        var error = Error.Validation("VALIDATION_ERROR", "Dados inválidos.", ["Nome é obrigatório."]);

        error.Type.ShouldBe(ErrorType.Validation);
        error.Issues.ShouldBe(["Nome é obrigatório."]);
    }
}
