using System.Security.Cryptography;
using Floruna.SharedKernel;

namespace Floruna.Domain.Platform;

public enum TenantStatus
{
    Active = 0,
    Suspended = 1,
}

/// <summary>
/// A loja (tenant). Raiz de agregado do contexto Platform.
/// </summary>
/// <remarks>
/// <para>Repare no que NÃO existe aqui: setters públicos. O estado só muda
/// por métodos com nome de negócio (<see cref="Suspend"/>,
/// <see cref="StartTrial"/>), que protegem as invariantes. Isso é o
/// oposto do "modelo anêmico" (classe só com get/set e regra espalhada
/// em services).</para>
/// <para>O Tenant NÃO implementa <see cref="ITenantOwned"/>: ele é a própria
/// fronteira, uma entidade global da plataforma.</para>
/// </remarks>
public sealed class Tenant : AggregateRoot<TenantId>
{
    public const int TrialDays = 14;
    public const int DunningGraceDays = 7;

    // Construtor para o EF Core (materialização). Não usar no código.
    private Tenant()
        : base(default)
    {
        Name = string.Empty;
        Slug = null!;
        ReferralCode = string.Empty;
        Currency = Money.DefaultCurrency;
        TimeZone = string.Empty;
        Locale = string.Empty;
    }

    private Tenant(TenantId id, string name, TenantSlug slug, DateTimeOffset now)
        : base(id)
    {
        Name = name;
        Slug = slug;
        Status = TenantStatus.Active;
        Plan = TenantPlan.Free;
        ReferralCode = $"FLORA-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
        Currency = Money.DefaultCurrency;
        TimeZone = "America/Sao_Paulo";
        Locale = "pt-BR";
        CreatedAt = now;
    }

    public string Name { get; private set; }

    public TenantSlug Slug { get; private set; }

    public TenantStatus Status { get; private set; }

    /// <summary>O que foi PAGO. Para o que vale agora, use <see cref="EffectivePlan"/>.</summary>
    public TenantPlan Plan { get; private set; }

    public DateTimeOffset? TrialEndsAt { get; private set; }

    public DateTimeOffset? PastDueSince { get; private set; }

    /// <summary>Domínio próprio. Só roteia tráfego depois de verificado (prova de posse via DNS).</summary>
    public string? CustomDomain { get; private set; }

    public bool CustomDomainVerified { get; private set; }

    public string ReferralCode { get; private set; }

    public string Currency { get; private set; }

    public string TimeZone { get; private set; }

    public string Locale { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Factory method: única forma de criar uma loja válida.</summary>
    public static Result<Tenant> Create(string? name, string? slug, DateTimeOffset now)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is < 2 or > 80)
        {
            return TenantErrors.NameInvalid;
        }

        var slugResult = TenantSlug.Create(slug);
        if (slugResult.IsFailure)
        {
            return slugResult.Error;
        }

        var tenant = new Tenant(TenantId.New(), trimmedName, slugResult.Value, now);
        tenant.Raise(new TenantCreated(tenant.Id, tenant.Slug.Value, now));
        return tenant;
    }

    /// <summary>Teste grátis do plano Pro. Não altera <see cref="Plan"/> (que reflete só o que foi pago).</summary>
    public void StartTrial(DateTimeOffset now) => TrialEndsAt = now.AddDays(TrialDays);

    public bool InTrial(DateTimeOffset now) => Plan == TenantPlan.Free && TrialEndsAt > now;

    /// <summary>
    /// Plano que vale AGORA (equivale ao virtual <c>effectivePlan</c> do Nest):
    /// pago continua valendo na carência de inadimplência; depois, cai para free.
    /// Teste grátis vigente vale como Pro.
    /// </summary>
    public TenantPlan EffectivePlan(DateTimeOffset now)
    {
        if (Plan != TenantPlan.Free)
        {
            return PastDueSince is null || now <= PastDueSince.Value.AddDays(DunningGraceDays)
                ? Plan
                : TenantPlan.Free;
        }

        return InTrial(now) ? TenantPlan.Pro : TenantPlan.Free;
    }

    public void Suspend()
    {
        if (Status == TenantStatus.Suspended)
        {
            return; // idempotente
        }

        Status = TenantStatus.Suspended;
    }

    public void Reactivate() => Status = TenantStatus.Active;

    public void MarkPastDue(DateTimeOffset since) => PastDueSince ??= since;
}

public sealed record TenantCreated(TenantId TenantId, string Slug, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
