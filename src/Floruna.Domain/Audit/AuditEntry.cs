using Floruna.SharedKernel;

namespace Floruna.Domain.Audit;

/// <summary>
/// Registro de auditoria de ações sensíveis (impersonação do suporte, troca
/// de domínio, gestão da equipe). Append-only: nunca é alterado nem apagado.
/// Pertence à loja AFETADA, para o dono ver quando o suporte entrou.
/// </summary>
public sealed class AuditEntry : Entity<Guid>, ITenantOwned
{
    private AuditEntry()
        : base(Guid.Empty)
    {
        Action = string.Empty;
        ActorRole = string.Empty;
        Ip = string.Empty;
        Entity = string.Empty;
        EntityId = string.Empty;
        Summary = string.Empty;
    }

    private AuditEntry(TenantId tenantId, string action, Guid? actorId, string actorRole, Guid? impersonatedBy, string ip, string entity, string entityId, string summary, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TenantId = tenantId;
        Action = action;
        ActorId = actorId;
        ActorRole = actorRole;
        ImpersonatedBy = impersonatedBy;
        Ip = ip;
        Entity = entity;
        EntityId = entityId;
        Summary = summary;
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>Ação no formato do Nest: <c>support.impersonate</c>, <c>domain.changed</c>, <c>team.changed</c>.</summary>
    public string Action { get; private set; }

    public Guid? ActorId { get; private set; }

    public string ActorRole { get; private set; }

    /// <summary>Preenchido quando a ação foi feita numa sessão de suporte.</summary>
    public Guid? ImpersonatedBy { get; private set; }

    public string Ip { get; private set; }

    public string Entity { get; private set; }

    public string EntityId { get; private set; }

    public string Summary { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditEntry Record(TenantId tenantId, string action, AuditActor actor, string entity, string entityId, string summary, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEntry(tenantId, action, actor.UserId, actor.Role, actor.ImpersonatedBy, actor.Ip, entity, entityId, summary, now);
    }
}

/// <summary>Quem fez a ação.</summary>
public sealed record AuditActor(Guid? UserId, string Role, Guid? ImpersonatedBy, string Ip);

public static class AuditActions
{
    public const string SupportImpersonate = "support.impersonate";
    public const string DomainChanged = "domain.changed";
    public const string TeamChanged = "team.changed";
    public const string PasswordReset = "auth.password_reset";
}

public interface IAuditRepository
{
    void Add(AuditEntry entry);
}
