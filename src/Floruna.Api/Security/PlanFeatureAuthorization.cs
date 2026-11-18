using Floruna.Application.Abstractions;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Microsoft.AspNetCore.Authorization;

namespace Floruna.Api.Security;

/// <summary>
/// <c>[RequiresPlanFeature(PlanFeature.Pdv)]</c>: bloqueia o endpoint se o
/// plano EFETIVO da loja não inclui o recurso (equivale ao <c>@PlanFeature</c> do Nest).
/// </summary>
/// <remarks>
/// Usa <see cref="IAuthorizationRequirementData"/> (.NET 8+): o próprio
/// atributo carrega o requisito, sem precisar registrar uma policy por recurso.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresPlanFeatureAttribute(PlanFeature feature) : AuthorizeAttribute, IAuthorizationRequirementData
{
    public PlanFeature Feature { get; } = feature;

    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new PlanFeatureRequirement(Feature)];
}

public sealed record PlanFeatureRequirement(PlanFeature Feature) : IAuthorizationRequirement;

internal sealed class PlanFeatureHandler(ITenantContext tenantContext) : AuthorizationHandler<PlanFeatureRequirement>
{
    public const string UpgradeRequiredMessage = "Este recurso não está incluído no seu plano atual.";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlanFeatureRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var plan = tenantContext.Tenant?.EffectivePlan ?? TenantPlan.Free;
        if (context.User.IsInRole(Roles.SuperAdmin) || PlanCatalog.For(plan).Allows(requirement.Feature))
        {
            context.Succeed(requirement);
        }
        else
        {
            // O motivo vira o código PLAN_UPGRADE_REQUIRED no envelope (ver ApiAuthorizationResultHandler).
            context.Fail(new AuthorizationFailureReason(this, UpgradeRequiredMessage));
        }

        return Task.CompletedTask;
    }
}
