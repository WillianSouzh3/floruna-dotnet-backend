using System.Text.RegularExpressions;
using Floruna.Application.Abstractions;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Floruna.Infrastructure.Tenancy;

/// <summary>
/// Resolve a loja pelo host, com as MESMAS regras do
/// <c>TenantHostResolverService</c> do Nest, e cache de 60 s (Cache-Aside).
/// </summary>
/// <remarks>
/// <para>SINGLETON (o cache precisa sobreviver entre requisições). Por isso
/// NÃO recebe o DbContext no construtor: o DbContext é scoped, e um singleton
/// que o capturasse usaria a mesma instância para sempre (captive dependency).
/// Ele cria um escopo curto só para a consulta.</para>
/// <para>Em várias réplicas, cada uma tem seu cache; o TTL curto é a rede de
/// segurança. Com Redis (Fase 7), a invalidação passa a ser distribuída.</para>
/// </remarks>
internal sealed partial class TenantHostResolver(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    IOptions<PlatformSettings> options,
    TimeProvider clock) : ITenantResolver, IDisposable
{
    private CancellationTokenSource _reset = new();

    public async Task<ResolvedTenant?> ResolveByHostAsync(string host, CancellationToken cancellationToken)
    {
        var lookup = Classify(Normalize(host));
        if (lookup is null)
        {
            return null;
        }

        var key = $"tenant:{lookup.Value.Kind}:{lookup.Value.Value}";
        if (cache.TryGetValue(key, out ResolvedTenant? cached))
        {
            return cached;
        }

        var resolved = await LoadAsync(lookup.Value, cancellationToken);
        cache.Set(key, resolved, new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(options.Value.CacheTtl)
            .AddExpirationToken(new CancellationChangeToken(_reset.Token)));
        return resolved;
    }

    public void Invalidate()
    {
        var previous = Interlocked.Exchange(ref _reset, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose() => _reset.Dispose();

    /// <summary>Regras do Nest, em ordem.</summary>
    internal (string Kind, string Value)? Classify(string host)
    {
        var platform = options.Value.Domain.ToLowerInvariant();
        var defaultSlug = options.Value.DefaultTenantSlug;

        if (host.Length == 0 || host == platform || host == "localhost" || IsIpAddress(host))
        {
            return ("slug", defaultSlug);
        }

        // Desenvolvimento: loja.localhost:3000 equivale a loja.<dominio>.
        if (host.EndsWith(".localhost", StringComparison.Ordinal))
        {
            var sub = host[..^".localhost".Length];
            return sub.Length == 0 || sub.Contains('.', StringComparison.Ordinal) ? ("slug", defaultSlug) : ("slug", sub);
        }

        if (platform.Length > 0 && host.EndsWith("." + platform, StringComparison.Ordinal))
        {
            var sub = host[..^(platform.Length + 1)];
            if (sub.Contains('.', StringComparison.Ordinal))
            {
                return null; // a.b.floruna.com.br não é loja
            }

            return ("slug", sub);
        }

        return ("domain", host);
    }

    internal static string Normalize(string? raw)
    {
        var host = (raw ?? string.Empty).Trim().ToLowerInvariant();
        host = PortSuffix().Replace(host, string.Empty);
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private async Task<ResolvedTenant?> LoadAsync((string Kind, string Value) lookup, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var tenants = db.Tenants.AsNoTracking();

        var tenant = lookup.Kind == "slug"
            ? await tenants.FirstOrDefaultAsync(t => t.Slug == TenantSlug.FromPersistence(lookup.Value), cancellationToken)
            : await FindByCustomDomainAsync(tenants, lookup.Value, cancellationToken);

        return tenant is null
            ? null
            : new ResolvedTenant(tenant.Id, tenant.Slug.Value, tenant.Status, tenant.EffectivePlan(clock.GetUtcNow()), tenant.TimeZone);
    }

    /// <summary>
    /// O host chega SEM "www." (a normalização remove), mas o lojista pode ter
    /// cadastrado <c>www.loja.com.br</c>. Procura as duas formas; sem isso, um
    /// domínio cadastrado com www nunca roteia (combinação de regras herdada do Nest).
    /// </summary>
    private static Task<Tenant?> FindByCustomDomainAsync(IQueryable<Tenant> tenants, string host, CancellationToken cancellationToken)
    {
        var withWww = "www." + host;
        return tenants
            .Where(t => (t.CustomDomain == host || t.CustomDomain == withWww) && t.CustomDomainVerified)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static bool IsIpAddress(string host) =>
        Ipv4().IsMatch(host) || host.Contains(':', StringComparison.Ordinal);

    [GeneratedRegex(@":\d+$")]
    private static partial Regex PortSuffix();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")]
    private static partial Regex Ipv4();
}
