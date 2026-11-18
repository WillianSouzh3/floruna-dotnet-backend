using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;

namespace Floruna.Application.Tenants;

/// <summary>Limites no formato do <c>PlanLimits</c> do Nest (o painel mostra "18 de 25 produtos").</summary>
public sealed record PlanLimitsResponse(
    int? MaxProducts,
    int MaxImagesPerProduct,
    int? MaxOrdersPerMonth,
    bool AllowPaymentGateway,
    bool AllowFiscal,
    bool AllowCustomDomain,
    long MaxStorageBytes,
    int MaxUsers,
    bool AllowPdv,
    bool AllowFinance,
    bool AllowCrm,
    bool AllowCommercial,
    bool AllowIntegrations,
    bool AllowWhatsapp,
    bool AllowAdvancedReports,
    bool AllowLoyalty)
{
    public static PlanLimitsResponse From(PlanLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new(
            limits.MaxProducts, limits.MaxImagesPerProduct, limits.MaxOrdersPerMonth,
            limits.Allows(PlanFeature.PaymentGateway), limits.Allows(PlanFeature.Fiscal), limits.Allows(PlanFeature.CustomDomain),
            limits.MaxStorageBytes, limits.MaxUsers,
            limits.Allows(PlanFeature.Pdv), limits.Allows(PlanFeature.Finance), limits.Allows(PlanFeature.Crm),
            limits.Allows(PlanFeature.Commercial), limits.Allows(PlanFeature.Integrations), limits.Allows(PlanFeature.Whatsapp),
            limits.Allows(PlanFeature.AdvancedReports), limits.Allows(PlanFeature.Loyalty));
    }
}

public sealed record UsageResponse(int Products, int OrdersThisMonth);

public sealed record ReferralResponse(string Code);

/// <summary>
/// <c>GET /tenants/me</c>: os campos de <see cref="TenantResponse"/> + plano, limites e consumo
/// (o Nest espalha <c>toSafeTenant</c> no mesmo objeto, então aqui também é plano).
/// </summary>
public sealed record CurrentTenantResponse(
    string Id, string Name, string Slug, string? CustomDomain, string CustomDomainStatus, string Status,
    string Plan, string EffectivePlan, bool InTrial, string? TrialEndsAt, string? PastDueSince,
    bool OnboardingCompleted, string? GoLiveAt, bool Live, TenantProfileResponse Profile,
    string PlanLabel, PlanLimitsResponse Limits, UsageResponse Usage, bool AiCopyEnabled, ReferralResponse Referral);

public sealed record GetCurrentTenantQuery : IQuery<CurrentTenantResponse>;

internal sealed class GetCurrentTenantHandler(ITenantContext tenantContext, ITenantRepository tenants, TimeProvider clock)
    : IQueryHandler<GetCurrentTenantQuery, CurrentTenantResponse>
{
    private static readonly Dictionary<TenantPlan, string> Labels = new()
    {
        [TenantPlan.Free] = "Grátis",
        [TenantPlan.Essential] = "Essencial",
        [TenantPlan.Pro] = "Profissional",
        [TenantPlan.Complete] = "Completo",
    };

    public async Task<Result<CurrentTenantResponse>> Handle(GetCurrentTenantQuery request, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Tenant is { } current ? await tenants.GetByIdAsync(current.Id, cancellationToken) : null;
        if (tenant is null)
        {
            return TenantErrors.NotResolved;
        }

        var now = clock.GetUtcNow();
        var effective = tenant.EffectivePlan(now);
        var t = TenantResponse.From(tenant, now);

        // Consumo: produtos entram na Fase 2 (catálogo) e pedidos na Fase 4.
        return new CurrentTenantResponse(
            t.Id, t.Name, t.Slug, t.CustomDomain, t.CustomDomainStatus, t.Status, t.Plan, t.EffectivePlan, t.InTrial,
            t.TrialEndsAt, t.PastDueSince, t.OnboardingCompleted, t.GoLiveAt, t.Live, t.Profile,
            Labels[effective], PlanLimitsResponse.From(PlanCatalog.For(effective)), new UsageResponse(0, 0),
            AiCopyEnabled: false, new ReferralResponse(tenant.ReferralCode));
    }
}
