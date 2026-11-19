using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Payments;

public sealed class PaymentIntentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly PaymentEvidence Receipt = new("pix_receipt", "E1234567890", "conferido no app do banco");

    private static PaymentIntent Intent(long amount = 12_550) =>
        PaymentIntent.Open(TenantId.New(), OrderId.New(), amount, PaymentRail.MercadoPago, "000201...", "mp-1", false, Now);

    [Fact]
    public void Approval_marks_paid_once_and_raises_the_event()
    {
        var intent = Intent();

        intent.RecordProviderStatus(ProviderPaymentStatus.Approved, 12_550, "mp:1:approved", Now).Value.ShouldBeTrue();
        intent.RecordProviderStatus(ProviderPaymentStatus.Approved, 12_550, "mp:1:approved", Now).Value.ShouldBeFalse(); // mesma notificação

        intent.Status.ShouldBe(PaymentIntentStatus.Paid);
        intent.PaidAt.ShouldBe(Now);
        intent.Transactions.Count.ShouldBe(1);
        intent.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PaymentConfirmed>();
    }

    [Fact]
    public void A_late_pending_notice_never_regresses_a_paid_intent()
    {
        var intent = Intent();
        intent.RecordProviderStatus(ProviderPaymentStatus.Approved, 12_550, "a", Now);

        intent.RecordProviderStatus(ProviderPaymentStatus.Pending, 12_550, "b", Now);

        intent.Status.ShouldBe(PaymentIntentStatus.Paid);
        intent.Transactions.Count.ShouldBe(2); // o fato fica no livro, o estado não volta
    }

    [Fact]
    public void Underpayment_is_not_confirmed_and_is_flagged()
    {
        var intent = Intent();

        intent.RecordProviderStatus(ProviderPaymentStatus.Approved, 12_000, "a", Now).Value.ShouldBeFalse();

        intent.Status.ShouldBe(PaymentIntentStatus.Pending);
        intent.ReconciliationIssue.ShouldBe("underpaid");
    }

    [Fact]
    public void Rejection_fails_a_pending_intent()
    {
        var intent = Intent();

        intent.RecordProviderStatus(ProviderPaymentStatus.Rejected, 12_550, "a", Now);

        intent.Status.ShouldBe(PaymentIntentStatus.Failed);
    }

    [Fact]
    public void Manual_confirmation_requires_the_exact_amount_and_keeps_the_evidence()
    {
        var intent = Intent();

        intent.ConfirmManually(12_000, Receipt, null, Guid.NewGuid(), "k1", Now).Error.Code.ShouldBe("VALIDATION_ERROR");
        intent.ConfirmManually(12_550, Receipt, "E1234567890", Guid.NewGuid(), "k1", Now).Value.ShouldBeTrue();
        intent.ConfirmManually(12_550, Receipt, "E1234567890", Guid.NewGuid(), "k1", Now).Value.ShouldBeFalse(); // replay
        intent.ConfirmManually(12_550, Receipt, null, Guid.NewGuid(), "k2", Now).Error.ShouldBe(PaymentErrors.AlreadyPaid);

        var transaction = intent.Transactions.ShouldHaveSingleItem();
        transaction.Source.ShouldBe(TransactionSource.Manual);
        transaction.Evidence.ShouldBe(Receipt);
    }
}
