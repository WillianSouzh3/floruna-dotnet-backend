using Floruna.Domain.Catalog;
using Floruna.Domain.Inventory;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Inventory;

public sealed class FefoAndStockItemTests
{
    private static readonly DateTimeOffset Today = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private static StockItem NewItem() => StockItem.Open(ProductId.New(), TenantId.New());

    private static Guid LotId(StockItem item, int index) => item.Lots[index].Id;

    // ── FEFO puro ────────────────────────────────────────────────────────

    [Fact]
    public void Sale_consumes_the_lot_expiring_first_and_non_perishable_last()
    {
        var item = NewItem();
        item.Receive(5, Today.AddDays(10), 100, "", "", Today.AddDays(-3));   // [0] vence dia 11
        item.Receive(5, null, 100, "", "", Today.AddDays(-5));                // [1] sem validade
        item.Receive(5, Today.AddDays(4), 100, "", "", Today.AddDays(-1));    // [2] vence dia 5

        var plan = FefoAllocator.Plan(item.Lots, 7, WithdrawalPurpose.Sale, Today);

        plan.Allocations.Select(a => (a.LotId, a.Quantity)).ShouldBe([(LotId(item, 2), 5), (LotId(item, 0), 2)]);
    }

    [Fact]
    public void Same_expiry_breaks_tie_by_oldest_received()
    {
        var item = NewItem();
        item.Receive(2, Today.AddDays(3), null, "", "", Today);               // [0] mais novo
        item.Receive(2, Today.AddDays(3), null, "", "", Today.AddDays(-2));   // [1] mais antigo

        FefoAllocator.Plan(item.Lots, 1, WithdrawalPurpose.Sale, Today).Allocations[0].LotId.ShouldBe(LotId(item, 1));
    }

    [Fact]
    public void Expired_lots_are_never_sold_but_are_discarded_first()
    {
        var item = NewItem();
        item.Receive(3, Today.AddDays(-1), null, "", "", Today.AddDays(-6));  // [0] vencido (flor murcha)
        item.Receive(3, Today.AddDays(5), null, "", "", Today);               // [1] bom

        FefoAllocator.Plan(item.Lots, 4, WithdrawalPurpose.Sale, Today).IsSatisfied.ShouldBeFalse();
        FefoAllocator.Plan(item.Lots, 3, WithdrawalPurpose.Discard, Today).Allocations.ShouldHaveSingleItem().LotId.ShouldBe(LotId(item, 0));
    }

    [Fact]
    public void Plan_is_all_or_nothing()
    {
        var item = NewItem();
        item.Receive(2, null, null, "", "", Today);

        var plan = FefoAllocator.Plan(item.Lots, 5, WithdrawalPurpose.Sale, Today);

        plan.Allocations.ShouldBeEmpty();
        plan.Shortfall.ShouldBe(3);
    }

    // ── Agregado StockItem ───────────────────────────────────────────────

    [Fact]
    public void On_hand_is_always_the_sum_of_the_lots()
    {
        var item = NewItem();
        item.Receive(10, null, null, "", "", Today);
        item.Receive(5, Today.AddDays(2), null, "", "", Today);

        item.RegisterLoss(3, Today).IsSuccess.ShouldBeTrue();

        item.OnHand.ShouldBe(12);
        item.OnHand.ShouldBe(item.Lots.Sum(l => l.Remaining));
    }

    [Fact]
    public void Available_excludes_expired_units_and_reservations()
    {
        var item = NewItem();
        item.Receive(4, Today.AddDays(-1), null, "", "", Today.AddDays(-5));
        item.Receive(6, Today.AddDays(3), null, "", "", Today);
        item.Hold(2, Today);

        item.OnHand.ShouldBe(10);
        item.AvailableAt(Today).ShouldBe(4); // 6 bons − 2 reservados
    }

    [Fact]
    public void Loss_and_negative_adjustment_cannot_make_stock_negative()
    {
        var item = NewItem();
        item.Receive(2, null, null, "", "", Today);

        item.RegisterLoss(3, Today).Error.ShouldBe(InventoryErrors.LossExceedsStock);
        item.Adjust(-3, "", "", Today).Error.ShouldBe(InventoryErrors.NegativeAdjustment);
        item.Adjust(0, "", "", Today).Error.ShouldBe(InventoryErrors.ZeroAdjustment);
        item.OnHand.ShouldBe(2);
    }

    [Fact]
    public void Positive_adjustment_becomes_a_lot_without_expiry()
    {
        var item = NewItem();

        var change = item.Adjust(4, "", "", Today).Value;

        change.Type.ShouldBe(StockMovementType.Ajuste);
        item.Lots.ShouldHaveSingleItem().ExpiresAt.ShouldBeNull();
        item.Lots[0].Note.ShouldBe("Ajuste de inventário");
    }

    [Fact]
    public void Sale_cannot_take_units_reserved_for_someone_else()
    {
        var item = NewItem();
        item.Receive(5, null, null, "", "", Today);
        item.Hold(4, Today);

        item.Sell(2, Today).Error.ShouldBe(InventoryErrors.InsufficientStock);
        item.Sell(1, Today).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Sale_from_own_reservation_consumes_the_hold()
    {
        var item = NewItem();
        item.Receive(5, null, 300, "", "", Today);
        item.Hold(3, Today);

        var change = item.Sell(3, Today, fromReservation: 3).Value;

        item.Reserved.ShouldBe(0);
        item.OnHand.ShouldBe(2);
        change.Allocations.ShouldHaveSingleItem().UnitCostCents.ShouldBe(300); // custo do lote: base do COGS
    }

    [Fact]
    public void Hold_beyond_available_is_refused()
    {
        var item = NewItem();
        item.Receive(1, null, null, "", "", Today);

        item.Hold(2, Today).Error.ShouldBe(InventoryErrors.InsufficientStock);
    }

    // ── Reserva ──────────────────────────────────────────────────────────

    [Fact]
    public void Reservation_settles_only_once()
    {
        var reservation = StockReservation.Hold(TenantId.New(), ProductId.New(), 2, ReservationOwnerType.Order, "pedido-1", "k1", Today.AddMinutes(30));

        reservation.Release("cliente desistiu", Today).ShouldBeTrue();
        reservation.Release("de novo", Today).ShouldBeFalse();
        reservation.Commit(Today).ShouldBeFalse();
        reservation.State.ShouldBe(ReservationState.Released);
    }

    [Fact]
    public void Reservation_expires_only_after_its_deadline()
    {
        var reservation = StockReservation.Hold(TenantId.New(), ProductId.New(), 1, ReservationOwnerType.Order, "p", "k", Today.AddMinutes(30));

        reservation.Expire(Today.AddMinutes(29)).ShouldBeFalse();
        reservation.Expire(Today.AddMinutes(30)).ShouldBeTrue();
        reservation.State.ShouldBe(ReservationState.Expired);
    }
}
