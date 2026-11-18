using Floruna.Domain.Platform;

namespace Floruna.Domain.UnitTests.Platform;

public sealed class TenantTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static Tenant NewTenant() => Tenant.Create("Flora & Lis", "flora-lis", Now).Value;

    [Fact]
    public void New_tenant_is_active_on_free_plan_with_defaults()
    {
        var tenant = NewTenant();

        tenant.Status.ShouldBe(TenantStatus.Active);
        tenant.Plan.ShouldBe(TenantPlan.Free);
        tenant.Currency.ShouldBe("BRL");
        tenant.TimeZone.ShouldBe("America/Sao_Paulo");
        tenant.ReferralCode.ShouldMatch("^FLORA-[0-9A-F]{8}$");
        tenant.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<TenantCreated>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    public void Name_must_have_2_to_80_characters(string name)
    {
        Tenant.Create(name, "flora-lis", Now).Error.ShouldBe(TenantErrors.NameInvalid);
    }

    [Fact]
    public void Trial_gives_pro_for_14_days_without_changing_the_paid_plan()
    {
        var tenant = NewTenant();
        tenant.StartTrial(Now);

        tenant.Plan.ShouldBe(TenantPlan.Free);
        tenant.InTrial(Now).ShouldBeTrue();
        tenant.EffectivePlan(Now.AddDays(13)).ShouldBe(TenantPlan.Pro);
        tenant.EffectivePlan(Now.AddDays(14)).ShouldBe(TenantPlan.Free);
    }

    [Fact]
    public void Without_trial_effective_plan_is_free()
    {
        NewTenant().EffectivePlan(Now).ShouldBe(TenantPlan.Free);
    }

    [Fact]
    public void Suspend_is_idempotent_and_reactivate_restores()
    {
        var tenant = NewTenant();

        tenant.Suspend();
        tenant.Suspend();
        tenant.Status.ShouldBe(TenantStatus.Suspended);

        tenant.Reactivate();
        tenant.Status.ShouldBe(TenantStatus.Active);
    }

    [Fact]
    public void Past_due_keeps_the_first_date()
    {
        var tenant = NewTenant();

        tenant.MarkPastDue(Now);
        tenant.MarkPastDue(Now.AddDays(3));

        tenant.PastDueSince.ShouldBe(Now);
    }
}
