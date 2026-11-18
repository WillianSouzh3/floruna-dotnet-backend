using System.Globalization;
using Floruna.Domain.Platform;

namespace Floruna.Application.Tenants;

/// <summary>
/// Loja como o front a conhece (<c>toSafeTenant</c> do Nest). Os campos que
/// ainda não existem no .NET (onboarding, go-live, perfil) saem com o valor
/// padrão do Nest para o front não quebrar.
/// </summary>
public sealed record TenantResponse(
    string Id,
    string Name,
    string Slug,
    string? CustomDomain,
    string CustomDomainStatus,
    string Status,
    string Plan,
    string EffectivePlan,
    bool InTrial,
    string? TrialEndsAt,
    string? PastDueSince,
    bool OnboardingCompleted,
    string? GoLiveAt,
    bool Live,
    TenantProfileResponse Profile)
{
    public static TenantResponse From(Tenant tenant, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return new TenantResponse(
            tenant.Id.ToString(),
            tenant.Name,
            tenant.Slug.Value,
            tenant.CustomDomain,
            tenant.CustomDomainVerified ? "verified" : "pending",
            ToContract(tenant.Status),
            ToContract(tenant.Plan),
            ToContract(tenant.EffectivePlan(now)),
            tenant.InTrial(now),
            Iso(tenant.TrialEndsAt),
            Iso(tenant.PastDueSince),
            OnboardingCompleted: false,
            GoLiveAt: null,
            Live: false,
            new TenantProfileResponse(null, [], null, string.Empty));
    }

    /// <summary>Enums viram as strings do contrato (<c>free</c>, <c>active</c>...).</summary>
    public static string ToContract(Enum value) =>
        value.ToString().ToLowerInvariant();

    private static string? Iso(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

public sealed record TenantProfileResponse(string? Personality, IReadOnlyList<string> Occasions, string? Goal, string Region);

public sealed record SignupResponse(TenantResponse Tenant);
