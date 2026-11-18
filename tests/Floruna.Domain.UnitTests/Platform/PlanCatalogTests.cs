using Floruna.Domain.Platform;

namespace Floruna.Domain.UnitTests.Platform;

/// <summary>Trava a tabela de planos (RN-04) igual ao <c>tenants/plans.ts</c> do Nest.</summary>
public sealed class PlanCatalogTests
{
    [Theory]
    [InlineData(TenantPlan.Free, 25, 3, 30, 1)]
    [InlineData(TenantPlan.Essential, 150, 5, 300, 2)]
    [InlineData(TenantPlan.Pro, 1_000, 8, 1_500, 5)]
    [InlineData(TenantPlan.Complete, 5_000, 8, 5_000, 15)]
    public void Limits_match_the_current_platform(TenantPlan plan, int products, int images, int orders, int users)
    {
        var limits = PlanCatalog.For(plan);

        limits.MaxProducts.ShouldBe(products);
        limits.MaxImagesPerProduct.ShouldBe(images);
        limits.MaxOrdersPerMonth.ShouldBe(orders);
        limits.MaxUsers.ShouldBe(users);
    }

    [Theory]
    [InlineData(TenantPlan.Free, PlanFeature.PaymentGateway, false)]
    [InlineData(TenantPlan.Essential, PlanFeature.PaymentGateway, true)]
    [InlineData(TenantPlan.Essential, PlanFeature.CustomDomain, true)]
    [InlineData(TenantPlan.Essential, PlanFeature.Pdv, false)]
    [InlineData(TenantPlan.Pro, PlanFeature.Pdv, true)]
    [InlineData(TenantPlan.Pro, PlanFeature.Loyalty, true)]
    [InlineData(TenantPlan.Pro, PlanFeature.Fiscal, false)]
    [InlineData(TenantPlan.Pro, PlanFeature.Integrations, false)]
    [InlineData(TenantPlan.Complete, PlanFeature.Fiscal, true)]
    [InlineData(TenantPlan.Complete, PlanFeature.Integrations, true)]
    public void Features_are_unlocked_by_plan(TenantPlan plan, PlanFeature feature, bool allowed)
    {
        PlanCatalog.For(plan).Allows(feature).ShouldBe(allowed);
    }

    [Fact]
    public void Complete_plan_includes_every_feature()
    {
        Enum.GetValues<PlanFeature>().ShouldAllBe(f => PlanCatalog.For(TenantPlan.Complete).Allows(f));
    }
}
