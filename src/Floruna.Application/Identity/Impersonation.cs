using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Audit;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.Extensions.Options;

namespace Floruna.Application.Identity;

public sealed record ImpersonationGrantResponse(string Code, string StoreUrl);

/// <summary>
/// Suporte "entra como a loja" (<c>POST /auth/admin/impersonate/:tenantId</c>, só superadmin).
/// </summary>
/// <remarks>
/// <para>Não devolve um token direto: devolve um CÓDIGO de uso único (60 s) que o
/// navegador troca no host DA LOJA. Assim o token nasce já preso àquela loja e
/// o isolamento normal continua valendo.</para>
/// <para>Ação auditada NA LOJA afetada: o dono vê que o suporte entrou.</para>
/// </remarks>
public sealed record ImpersonateTenantCommand(Guid TenantId, AuditActor Actor) : ICommand<ImpersonationGrantResponse>;

internal sealed class ImpersonateTenantHandler(
    TenantContext tenantContext,
    ITenantRepository tenants,
    IStaffUserRepository users,
    ISingleUseTokenRepository singleUseTokens,
    ICredentialHasher credentialHasher,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    IOptions<PlatformSettings> platform,
    TimeProvider clock) : ICommandHandler<ImpersonateTenantCommand, ImpersonationGrantResponse>
{
    public static readonly Error StoreNotFound = Error.NotFound("NOT_FOUND", "Loja não encontrada.");
    public static readonly Error NoMaster = Error.NotFound("NOT_FOUND", "A loja não tem um administrador master.");

    public async Task<Result<ImpersonationGrantResponse>> Handle(ImpersonateTenantCommand request, CancellationToken cancellationToken)
    {
        var target = await tenants.GetByIdAsync(new TenantId(request.TenantId), cancellationToken);
        if (target is null)
        {
            return StoreNotFound;
        }

        var master = await users.FindMasterOfTenantAsync(target.Id, cancellationToken);
        if (master is null)
        {
            return NoMaster;
        }

        var now = clock.GetUtcNow();
        var code = SecureRandom.HexToken();

        // O grant e a auditoria pertencem à loja ALVO, não à loja de onde o superadmin opera.
        using (tenantContext.Enter(new ResolvedTenant(target.Id, target.Slug.Value, target.Status, target.EffectivePlan(now))))
        {
            singleUseTokens.Add(SingleUseToken.Issue(
                target.Id, SingleUseTokenPurpose.Impersonation, master.Id, credentialHasher.HashOpaque(code), now, request.Actor.UserId));
            audit.Add(AuditEntry.Record(target.Id, AuditActions.SupportImpersonate, request.Actor, "tenant", target.Id.ToString(),
                $"Suporte entrou como a loja \"{target.Slug.Value}\"", now));
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var host = target.CustomDomainVerified && target.CustomDomain is not null
            ? target.CustomDomain
            : $"{target.Slug.Value}.{platform.Value.Domain}";
        return new ImpersonationGrantResponse(code, $"https://{host}");
    }
}

/// <summary>Troca o código de suporte por uma sessão curta, no host da loja (<c>POST /auth/admin/impersonation/exchange</c>).</summary>
public sealed record ExchangeImpersonationCommand(string Code) : ICommand<AuthResult>;

internal sealed class ExchangeImpersonationHandler(
    ITenantContext tenantContext,
    ISingleUseTokenRepository singleUseTokens,
    ICredentialHasher credentialHasher,
    IStaffUserRepository users,
    IAccessTokenIssuer tokens,
    TimeProvider clock) : ICommandHandler<ExchangeImpersonationCommand, AuthResult>
{
    public async Task<Result<AuthResult>> Handle(ExchangeImpersonationCommand request, CancellationToken cancellationToken)
    {
        if (tenantContext.Tenant is null)
        {
            return TenantErrors.NotResolved;
        }

        var grant = await singleUseTokens.TryConsumeAsync(
            SingleUseTokenPurpose.Impersonation, credentialHasher.HashOpaque(request.Code ?? string.Empty), clock.GetUtcNow(), cancellationToken);
        var master = grant is null ? null : await users.GetByIdAsync(grant.UserId, cancellationToken);
        if (grant?.IssuedBy is not { } superAdminId || master is null || master.Role != Roles.Master)
        {
            return IdentityApplicationErrors.ImpersonationGrantInvalid;
        }

        return AuthResults.For(master, tokens.IssueImpersonationFor(master, superAdminId));
    }
}
