using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Audit;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.Extensions.Options;

namespace Floruna.Application.Tenants;

public sealed record DnsRecord(string Name, string Value);

/// <summary>O que o lojista configura no DNS: TXT (prova de posse) e CNAME (aponta para a loja).</summary>
public sealed record DomainInstructions(string Domain, string Status, DnsRecord Txt, DnsRecord Cname, string? VerifiedAt);

public sealed record DomainStatusResponse(DomainInstructions? Instructions);

internal static class DomainInstructionsFactory
{
    public static DomainInstructions For(Tenant tenant, string platformDomain)
    {
        var domain = tenant.CustomDomain ?? string.Empty;
        return new DomainInstructions(
            domain,
            tenant.CustomDomainVerified ? "verified" : "pending",
            new DnsRecord(CustomDomainName.VerificationRecordFor(domain), tenant.CustomDomainToken ?? string.Empty),
            new DnsRecord(domain, $"{tenant.Slug.Value}.{platformDomain}"),
            tenant.CustomDomainVerifiedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>Base dos casos de uso de domínio: carregar a loja atual (Template Method enxuto via helper).</summary>
internal static class CurrentTenantLoader
{
    public static async Task<Tenant?> LoadAsync(ITenantContext context, ITenantRepository tenants, CancellationToken cancellationToken) =>
        context.Tenant is { } current ? await tenants.GetByIdAsync(current.Id, cancellationToken) : null;
}

/// <summary><c>GET /tenants/me/domain</c>.</summary>
public sealed record GetDomainStatusQuery : IQuery<DomainStatusResponse>;

internal sealed class GetDomainStatusHandler(ITenantContext context, ITenantRepository tenants, IOptions<PlatformSettings> platform)
    : IQueryHandler<GetDomainStatusQuery, DomainStatusResponse>
{
    public async Task<Result<DomainStatusResponse>> Handle(GetDomainStatusQuery request, CancellationToken cancellationToken)
    {
        var tenant = await CurrentTenantLoader.LoadAsync(context, tenants, cancellationToken);
        if (tenant is null)
        {
            return TenantErrors.NotResolved;
        }

        return new DomainStatusResponse(tenant.CustomDomain is null ? null : DomainInstructionsFactory.For(tenant, platform.Value.Domain));
    }
}

/// <summary><c>POST /tenants/me/domain</c>: reivindica e devolve o DNS a configurar. Ainda NÃO roteia.</summary>
public sealed record RequestCustomDomainCommand(string Domain, AuditActor Actor) : ICommand<DomainInstructions>;

internal sealed class RequestCustomDomainHandler(
    ITenantContext context,
    ITenantRepository tenants,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    ITenantResolver resolver,
    IOptions<PlatformSettings> platform,
    TimeProvider clock) : ICommandHandler<RequestCustomDomainCommand, DomainInstructions>
{
    public async Task<Result<DomainInstructions>> Handle(RequestCustomDomainCommand request, CancellationToken cancellationToken)
    {
        var tenant = await CurrentTenantLoader.LoadAsync(context, tenants, cancellationToken);
        if (tenant is null)
        {
            return TenantErrors.NotResolved;
        }

        var domain = CustomDomainName.Create(request.Domain, platform.Value.Domain);
        if (domain.IsFailure)
        {
            return domain.Error;
        }

        if (await tenants.IsDomainVerifiedByAnotherAsync(domain.Value.Value, tenant.Id, cancellationToken))
        {
            return CustomDomainErrors.TakenByAnotherStore;
        }

        tenant.RequestCustomDomain(domain.Value, $"flora-verify={SecureRandom.HexToken(16)}");
        audit.Add(AuditEntry.Record(tenant.Id, AuditActions.DomainChanged, request.Actor, "tenant", tenant.Id.ToString(),
            $"Domínio próprio solicitado: {domain.Value.Value}", clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        resolver.Invalidate(); // um domínio verificado antes deixa de rotear

        return DomainInstructionsFactory.For(tenant, platform.Value.Domain);
    }
}

/// <summary><c>POST /tenants/me/domain/verify</c>: consulta o TXT e, se conferir, ativa o domínio.</summary>
public sealed record VerifyCustomDomainCommand(AuditActor Actor) : ICommand<DomainInstructions>;

internal sealed class VerifyCustomDomainHandler(
    ITenantContext context,
    ITenantRepository tenants,
    IDnsTxtResolver dns,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    ITenantResolver resolver,
    IOptions<PlatformSettings> platform,
    TimeProvider clock) : ICommandHandler<VerifyCustomDomainCommand, DomainInstructions>
{
    public async Task<Result<DomainInstructions>> Handle(VerifyCustomDomainCommand request, CancellationToken cancellationToken)
    {
        var tenant = await CurrentTenantLoader.LoadAsync(context, tenants, cancellationToken);
        if (tenant is null)
        {
            return TenantErrors.NotResolved;
        }

        if (tenant.CustomDomain is null)
        {
            return CustomDomainErrors.NothingPending;
        }

        var record = CustomDomainName.VerificationRecordFor(tenant.CustomDomain);
        var lookup = await dns.ResolveAsync(record, cancellationToken);
        if (lookup.Status == TxtLookupStatus.NotFound)
        {
            return CustomDomainErrors.TxtNotFound(record);
        }

        if (lookup.Status == TxtLookupStatus.Failed)
        {
            return CustomDomainErrors.DnsUnavailable;
        }

        var now = clock.GetUtcNow();
        var verified = tenant.VerifyCustomDomain(lookup.Values, now);
        if (verified.IsFailure)
        {
            return verified.Error;
        }

        audit.Add(AuditEntry.Record(tenant.Id, AuditActions.DomainChanged, request.Actor, "tenant", tenant.Id.ToString(),
            $"Domínio próprio verificado: {tenant.CustomDomain}", now));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // Outra loja verificou o mesmo domínio entre o pedido e a verificação.
            return CustomDomainErrors.TakenByAnotherStore;
        }

        resolver.Invalidate();
        return DomainInstructionsFactory.For(tenant, platform.Value.Domain);
    }
}

/// <summary><c>DELETE /tenants/me/domain</c>.</summary>
public sealed record RemoveCustomDomainCommand(AuditActor Actor) : ICommand;

internal sealed class RemoveCustomDomainHandler(
    ITenantContext context,
    ITenantRepository tenants,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    ITenantResolver resolver,
    TimeProvider clock) : ICommandHandler<RemoveCustomDomainCommand>
{
    public async Task<Result> Handle(RemoveCustomDomainCommand request, CancellationToken cancellationToken)
    {
        var tenant = await CurrentTenantLoader.LoadAsync(context, tenants, cancellationToken);
        if (tenant is null)
        {
            return TenantErrors.NotResolved;
        }

        tenant.RemoveCustomDomain();
        audit.Add(AuditEntry.Record(tenant.Id, AuditActions.DomainChanged, request.Actor, "tenant", tenant.Id.ToString(),
            "Domínio próprio removido", clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        resolver.Invalidate();
        return Result.Success();
    }
}
