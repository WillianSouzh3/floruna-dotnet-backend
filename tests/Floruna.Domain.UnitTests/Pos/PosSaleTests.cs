using Floruna.Domain.Catalog;
using Floruna.Domain.Ordering;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Pos;

public sealed class PosSaleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private static PosPlacement Sale(long discount, params PosPayment[] payments) => new(
        TenantId.New(), null, new CustomerContact("Consumidor", "Não informado", null),
        [new OrderItem(new ProductId(Guid.NewGuid()), null, null, "Buquê", null, 10_000, 4_000, 2)], discount, payments, Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Counter_sale_is_born_paid_and_delivered_with_split_payment()
    {
        var order = Order.PlacePos(OrderId.New(), Sale(2_000, new PosPayment("dinheiro", 8_000), new PosPayment("pix", 10_000)), Now).Value.Order;

        order.Status.ShouldBe(OrderStatus.Entregue);
        order.PaymentStatus.ShouldBe("approved");
        order.Origin.ShouldBe("pdv");
        order.PaymentMethod.ShouldBe("multiplo");
        order.TotalCents.ShouldBe(18_000);
        order.CashReceivedCents.ShouldBe(8_000); // só a parte em dinheiro vai para a gaveta
    }

    [Fact]
    public void Payments_must_add_up_to_the_total()
    {
        Order.PlacePos(OrderId.New(), Sale(0, new PosPayment("dinheiro", 19_999)), Now).Error.Message.ShouldContain("não bate com o total");
    }

    [Fact]
    public void Unknown_payment_method_is_rejected()
    {
        Order.PlacePos(OrderId.New(), Sale(0, new PosPayment("cheque", 20_000)), Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Discount_cannot_exceed_the_items()
    {
        Order.PlacePos(OrderId.New(), Sale(25_000, new PosPayment("dinheiro", 0)), Now).Error.ShouldBe(OrderErrors.DiscountAboveItems);
    }
}
