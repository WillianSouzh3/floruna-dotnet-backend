using Floruna.Domain.Catalog;
using Floruna.Domain.Delivery;
using Floruna.Domain.Ordering;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Commerce;

public sealed class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static OrderPlacement Placement(long subtotal = 10_000, long discount = 0, long fee = 1_500, DeliveryMethod method = DeliveryMethod.Retirada) => new(
        TenantId.New(), "k1", new CustomerContact("Ana", "11999990000", "ana@x.com.br"), null,
        [new OrderItem(ProductId.New(), null, null, "Buquê", null, subtotal, 4_000, 1)],
        new OrderTotals(subtotal, discount, fee, 0), method, null, null, null, null, null, null, null, null, SubstitutionMode.AskMe);

    private static (Order Order, string Token) Placed() => Order.Place(OrderId.New(), Placement(), Now).Value;

    [Fact]
    public void Placed_order_is_pending_with_payment_deadline_and_event()
    {
        var (order, _) = Placed();

        order.Status.ShouldBe(OrderStatus.Pendente);
        order.TotalCents.ShouldBe(11_500);
        order.PaymentDeadline.ShouldBe(Now + Order.PaymentWindow);
        order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<OrderPlaced>();
    }

    [Fact]
    public void Delivery_requires_an_address_and_totals_must_make_sense()
    {
        Order.Place(OrderId.New(), Placement(method: DeliveryMethod.Entrega), Now).Error.ShouldBe(OrderErrors.AddressRequired);
        Order.Place(OrderId.New(), Placement(discount: 10_001), Now).Error.ShouldBe(OrderErrors.DiscountAboveItems);
        Order.Place(OrderId.New(), Placement(discount: 10_000, fee: 0), Now).Error.ShouldBe(OrderErrors.TotalMustBePositive);
    }

    [Fact]
    public void Public_token_opens_the_order_and_only_its_hash_is_stored()
    {
        var (order, token) = Placed();

        order.CanBeReadWith(token).ShouldBeTrue();
        order.CanBeReadWith(token + "x").ShouldBeFalse();
        order.CanBeReadWith(null).ShouldBeFalse();
        order.AccessTokenHash.ShouldNotBe(token);
    }

    [Theory]
    [InlineData(OrderStatus.Pendente, OrderStatus.Confirmado, true)]
    [InlineData(OrderStatus.Pendente, OrderStatus.Entregue, true)]     // avanço pode pular etapas
    [InlineData(OrderStatus.EmEntrega, OrderStatus.EmPreparo, false)]  // nunca volta
    [InlineData(OrderStatus.Confirmado, OrderStatus.Confirmado, false)]
    [InlineData(OrderStatus.EmEntrega, OrderStatus.Cancelado, true)]
    [InlineData(OrderStatus.Entregue, OrderStatus.Cancelado, false)]  // final
    [InlineData(OrderStatus.Cancelado, OrderStatus.Confirmado, false)] // final
    public void Status_machine_moves_forward_or_cancels_from_non_final(OrderStatus from, OrderStatus to, bool allowed)
    {
        Order.CanMove(from, to).ShouldBe(allowed);
    }

    [Fact]
    public void Same_idempotency_key_is_a_no_op_and_expected_state_guards_concurrent_edits()
    {
        var (order, _) = Placed();

        order.ChangeStatus(OrderStatus.Confirmado, null, "master", null, OrderStatus.Pendente, "clique-1", Now).Value.ShouldNotBeNull();
        order.ChangeStatus(OrderStatus.Confirmado, null, "master", null, OrderStatus.Pendente, "clique-1", Now).Value.ShouldBeNull(); // replay
        order.History.Count.ShouldBe(2);

        order.ChangeStatus(OrderStatus.EmPreparo, null, "gerente", null, OrderStatus.Pendente, "clique-2", Now).Error.Code.ShouldBe("CONFLICT");
    }

    [Fact]
    public void Abandoned_only_after_deadline_while_pending()
    {
        var (order, _) = Placed();

        order.IsAbandonedAt(Now + Order.PaymentWindow - TimeSpan.FromSeconds(1)).ShouldBeFalse();
        order.IsAbandonedAt(Now + Order.PaymentWindow).ShouldBeTrue();
    }
}
