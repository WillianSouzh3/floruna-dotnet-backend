using Floruna.Domain.Delivery;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Commerce;

public sealed class DeliveryCalendarTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly DateOnly MothersDayEve = new(2027, 5, 8); // sábado

    private static CapacityRule Rule(CapacityRuleKind kind, int capacity, DeliveryWindow? window = null, int cutoff = 0, Guid? zone = null, int? weekday = null) =>
        CapacityRule.Create(TenantId.New(), new CapacityRuleSpec(
            kind, kind == CapacityRuleKind.Recurring ? weekday ?? (int)MothersDayEve.DayOfWeek : null,
            kind == CapacityRuleKind.Recurring ? null : MothersDayEve, window, zone, capacity, cutoff, true, null)).Value;

    [Fact]
    public void Without_rules_capacity_is_unlimited()
    {
        DeliveryCalendar.Resolve([], MothersDayEve, DeliveryWindow.Tarde, null, SaoPaulo).Capacity.ShouldBeNull();
    }

    [Fact]
    public void Date_rule_beats_recurring_rule_regardless_of_creation_order()
    {
        var recurring = Rule(CapacityRuleKind.Recurring, 10);
        var mothersDay = Rule(CapacityRuleKind.Date, 40);

        DeliveryCalendar.Resolve([recurring, mothersDay], MothersDayEve, DeliveryWindow.Tarde, null, SaoPaulo).Capacity.ShouldBe(40);
        DeliveryCalendar.Resolve([mothersDay, recurring], MothersDayEve, DeliveryWindow.Tarde, null, SaoPaulo).Capacity.ShouldBe(40);
    }

    [Fact]
    public void Blackout_closes_the_date_over_any_other_rule()
    {
        var policy = DeliveryCalendar.Resolve([Rule(CapacityRuleKind.Date, 99), Rule(CapacityRuleKind.Blackout, 0)], MothersDayEve, DeliveryWindow.Manha, null, SaoPaulo);

        DeliveryCalendar.Check(policy, MothersDayEve.ToDateTime(TimeOnly.MinValue).AddDays(-10), 0).Error.ShouldBe(DeliveryCalendar.ClosedDate);
    }

    [Fact]
    public void Window_and_zone_specific_rules_win_and_set_the_counting_scope()
    {
        var zone = Guid.NewGuid();
        var general = Rule(CapacityRuleKind.Recurring, 10);
        var specific = Rule(CapacityRuleKind.Recurring, 3, DeliveryWindow.Noite, zone: zone);

        var policy = DeliveryCalendar.Resolve([general, specific], MothersDayEve, DeliveryWindow.Noite, zone, SaoPaulo);

        policy.Capacity.ShouldBe(3);
        policy.ScopeZoneId.ShouldBe(zone);
        DeliveryCalendar.Resolve([general, specific], MothersDayEve, DeliveryWindow.Noite, Guid.NewGuid(), SaoPaulo).ScopeZoneId.ShouldBeNull();
    }

    [Fact]
    public void Cutoff_is_counted_back_from_the_window_start_in_store_local_time()
    {
        var policy = DeliveryCalendar.Resolve([Rule(CapacityRuleKind.Date, 10, cutoff: 120)], MothersDayEve, DeliveryWindow.Tarde, null, SaoPaulo);

        // Tarde começa 13:00 em São Paulo (UTC−3) = 16:00 UTC; com 2 h de corte, fecha 14:00 UTC.
        policy.AcceptsUntil.ShouldBe(new DateTimeOffset(2027, 5, 8, 14, 0, 0, TimeSpan.Zero));
        DeliveryCalendar.Check(policy, new DateTimeOffset(2027, 5, 8, 13, 59, 0, TimeSpan.Zero), 0).IsSuccess.ShouldBeTrue();
        DeliveryCalendar.Check(policy, new DateTimeOffset(2027, 5, 8, 14, 0, 0, TimeSpan.Zero), 0).Error.ShouldBe(DeliveryCalendar.CutoffPassed);
    }

    [Fact]
    public void Full_window_is_refused()
    {
        var policy = DeliveryCalendar.Resolve([Rule(CapacityRuleKind.Date, 2)], MothersDayEve, DeliveryWindow.Manha, null, SaoPaulo);
        var early = new DateTimeOffset(2027, 5, 1, 0, 0, 0, TimeSpan.Zero);

        DeliveryCalendar.Check(policy, early, 1).IsSuccess.ShouldBeTrue();
        DeliveryCalendar.Check(policy, early, 2).Error.ShouldBe(DeliveryCalendar.SlotFull);
    }
}
