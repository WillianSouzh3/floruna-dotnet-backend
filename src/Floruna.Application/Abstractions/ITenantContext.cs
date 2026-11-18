using Floruna.Domain.Platform;
using Floruna.SharedKernel;

namespace Floruna.Application.Abstractions;

/// <summary>Fotografia da loja resolvida para a requisição (vem do cache, não do banco a cada request).</summary>
public sealed record ResolvedTenant(TenantId Id, string Slug, TenantStatus Status, TenantPlan EffectivePlan);

/// <summary>
/// A loja da requisição atual. Equivale ao AsyncLocalStorage do Nest
/// (<c>tenant-context.ts</c>), mas como serviço SCOPED do DI: uma
/// instância por requisição, sem estado global.
/// </summary>
public interface ITenantContext
{
    ResolvedTenant? Tenant { get; }

    /// <summary>O tenant atual ou exceção. Usado pela persistência: sem loja, não há consulta (fail-closed).</summary>
    TenantId RequiredTenantId { get; }
}

/// <summary>Lançada quando código tenta acessar dados de loja sem uma loja no contexto.</summary>
public sealed class TenantContextMissingException()
    : InvalidOperationException("Nenhuma loja no contexto da requisição. Acesso a dados de loja negado (fail-closed).");

/// <summary>
/// Implementação mutável, preenchida pelo middleware de resolução de loja
/// (ou por um job, ao processar dados de uma loja específica).
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public ResolvedTenant? Tenant { get; private set; }

    public TenantId RequiredTenantId => Tenant?.Id ?? throw new TenantContextMissingException();

    public void Set(ResolvedTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (Tenant is not null && Tenant.Id != tenant.Id)
        {
            throw new InvalidOperationException("A loja da requisição não pode ser trocada depois de definida.");
        }

        Tenant = tenant;
    }

    /// <summary>
    /// Entra no contexto de uma loja recém-criada (cadastro de loja) e sai ao
    /// descartar. Explícito de propósito, como o <c>runWithTenant</c> do Nest.
    /// </summary>
    public IDisposable Enter(ResolvedTenant tenant)
    {
        var previous = Tenant;
        Tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        return new Restore(this, previous);
    }

    private sealed class Restore(TenantContext context, ResolvedTenant? previous) : IDisposable
    {
        public void Dispose() => context.Tenant = previous;
    }
}
