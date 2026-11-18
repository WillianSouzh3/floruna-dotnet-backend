namespace Floruna.Domain.Platform;

/// <summary>Planos do SaaS. O valor serializado é o mesmo do Nest (<c>free</c>, <c>essential</c>...).</summary>
public enum TenantPlan
{
    Free = 0,
    Essential = 1,
    Pro = 2,
    Complete = 3,
}

/// <summary>Recursos liberados por plano (os <c>allow*</c> de <c>tenants/plans.ts</c>).</summary>
public enum PlanFeature
{
    PaymentGateway,
    Fiscal,
    CustomDomain,
    Pdv,
    Finance,
    Crm,
    Commercial,
    Integrations,
    Whatsapp,
    AdvancedReports,
    Loyalty,
}

/// <summary>Limites e recursos de um plano. Imutável.</summary>
/// <param name="MaxProducts">null = sem teto.</param>
/// <param name="MaxImagesPerProduct">Fotos por produto.</param>
/// <param name="MaxOrdersPerMonth">Fair-use: MEDIDO e exibido, nunca bloqueia o comprador (RN-05).</param>
/// <param name="MaxStorageBytes">Teto de armazenamento de imagens.</param>
/// <param name="MaxUsers">Usuários da equipe, incluindo o dono.</param>
/// <param name="Features">Recursos liberados.</param>
public sealed record PlanLimits(
    int? MaxProducts,
    int MaxImagesPerProduct,
    int? MaxOrdersPerMonth,
    long MaxStorageBytes,
    int MaxUsers,
    IReadOnlySet<PlanFeature> Features)
{
    public bool Allows(PlanFeature feature) => Features.Contains(feature);
}

/// <summary>
/// Tabela de planos (RN-04). Static e imutável: é DADO DE DOMÍNIO, não
/// configuração. Mudar um limite é decisão de negócio e passa por
/// code review e por teste, igual ao <c>PLAN_LIMITS</c> do Nest.
/// </summary>
public static class PlanCatalog
{
    private const long Mb = 1024L * 1024;
    private const long Gb = 1024 * Mb;

    private static readonly Dictionary<TenantPlan, PlanLimits> Limits = new()
    {
        [TenantPlan.Free] = new(25, 3, 30, 250 * Mb, 1, new HashSet<PlanFeature>()),
        [TenantPlan.Essential] = new(150, 5, 300, 1 * Gb, 2, new HashSet<PlanFeature>
        {
            PlanFeature.PaymentGateway, PlanFeature.CustomDomain,
        }),
        [TenantPlan.Pro] = new(1_000, 8, 1_500, 5 * Gb, 5, new HashSet<PlanFeature>
        {
            PlanFeature.PaymentGateway, PlanFeature.CustomDomain, PlanFeature.Pdv, PlanFeature.Finance,
            PlanFeature.Crm, PlanFeature.Commercial, PlanFeature.Whatsapp, PlanFeature.AdvancedReports,
            PlanFeature.Loyalty,
        }),
        [TenantPlan.Complete] = new(5_000, 8, 5_000, 20 * Gb, 15, new HashSet<PlanFeature>(Enum.GetValues<PlanFeature>())),
    };

    public static PlanLimits For(TenantPlan plan) => Limits[plan];
}
