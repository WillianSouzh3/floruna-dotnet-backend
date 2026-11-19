using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Payments;

public sealed class RefundTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static PaymentIntent PaidIntent(long amount = 10_000)
    {
        var intent = PaymentIntent.Open(TenantId.New(), OrderId.New(), amount, PaymentRail.MercadoPago, "000201...", "mp-1", false, Now);
        intent.RecordProviderStatus(ProviderPaymentStatus.Approved, amount, "approved-1", Now);
        return intent;
    }

    [Fact]
    public void Unpaid_intent_cannot_be_refunded()
    {
        var intent = PaymentIntent.Open(TenantId.New(), OrderId.New(), 10_000, PaymentRail.PixOffline, "000201...", null, false, Now);

        intent.ReserveRefund(1_000).Error.ShouldBe(PaymentErrors.NotRefundable);
    }

    [Fact]
    public void Reservations_in_flight_count_against_the_refundable_amount()
    {
        var intent = PaidIntent();

        intent.ReserveRefund(6_000).IsSuccess.ShouldBeTrue();

        // Ainda não confirmado no gateway, mas já "comprometido": dois reembolsos não passam do pago.
        intent.ReserveRefund(5_000).Error.Code.ShouldBe("VALIDATION_ERROR");
        intent.RefundableCents.ShouldBe(4_000);
    }

    [Fact]
    public void Completing_moves_the_reservation_to_refunded_and_full_refund_changes_status()
    {
        var intent = PaidIntent();
        intent.ReserveRefund(4_000);
        intent.CompleteRefund(4_000);

        intent.RefundedAmountCents.ShouldBe(4_000);
        intent.Status.ShouldBe(PaymentIntentStatus.Paid);

        intent.ReserveRefund(6_000);
        intent.CompleteRefund(6_000);
        intent.Status.ShouldBe(PaymentIntentStatus.Refunded);
        intent.RefundableCents.ShouldBe(0);
    }

    [Fact]
    public void Rejected_refund_releases_the_reservation()
    {
        var intent = PaidIntent();
        intent.ReserveRefund(10_000);

        intent.ReleaseRefund(10_000);

        intent.RefundableCents.ShouldBe(10_000);
    }

    [Fact]
    public void Unknown_outcome_keeps_the_refund_pending_with_backoff()
    {
        var refund = Refund.Request(TenantId.New(), Guid.NewGuid(), OrderId.New(), 5_000, RefundScope.Partial, RefundMethod.Gateway, "Flor murcha", "k1", [], false, "op@loja", Now);

        refund.Defer("timeout", Now);
        refund.Status.ShouldBe(RefundStatus.Pending);
        refund.NextAttemptAt.ShouldBe(Now.AddSeconds(30));

        refund.Defer("timeout", Now);
        refund.NextAttemptAt.ShouldBe(Now.AddSeconds(60)); // exponencial
        refund.Attempts.ShouldBe(2);
    }

    [Fact]
    public void Refund_finishes_once()
    {
        var refund = Refund.Request(TenantId.New(), Guid.NewGuid(), OrderId.New(), 5_000, RefundScope.Full, RefundMethod.Gateway, "Cancelamento", "k1", [], false, "op@loja", Now);

        refund.Succeed("mp-refund-9", Now).ShouldBeTrue();

        refund.Succeed("mp-refund-9", Now).ShouldBeFalse();
        refund.Fail("tarde demais", Now).ShouldBeFalse();
        refund.Status.ShouldBe(RefundStatus.Succeeded);
    }
}
