using Floruna.Application.Abstractions;
using Floruna.Application.Catalog;
using Microsoft.Extensions.Caching.Hybrid;

namespace Floruna.Infrastructure.Catalog;

/// <summary>
/// Cache-aside com <see cref="HybridCache"/> (.NET 9+): memória local (L1) e,
/// se configurado, Redis (L2). Protege contra "stampede": se 100 requisições
/// pedirem a mesma chave ao mesmo tempo, só UMA vai ao banco.
/// </summary>
/// <remarks>
/// Toda entrada leva a TAG da loja; qualquer escrita no catálogo da loja apaga
/// as entradas dela de uma vez. Com várias réplicas e sem Redis, cada réplica
/// pode servir até <see cref="Ttl"/> de dado antigo (consistência eventual
/// aceita para vitrine; o checkout SEMPRE relê o preço do banco).
/// </remarks>
internal sealed class HybridCatalogCache(HybridCache cache, ITenantContext tenantContext) : ICatalogCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static readonly HybridCacheEntryOptions Options = new() { Expiration = Ttl, LocalCacheExpiration = Ttl };

    public async Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.RequiredTenantId.ToString();
        return await cache.GetOrCreateAsync(
            $"catalog:{tenant}:{key}",
            factory,
            static (f, ct) => new ValueTask<T>(f(ct)),
            Options,
            [Tag(tenant)],
            cancellationToken);
    }

    public Task InvalidateAsync(CancellationToken cancellationToken) =>
        cache.RemoveByTagAsync(Tag(tenantContext.RequiredTenantId.ToString()), cancellationToken).AsTask();

    private static string Tag(string tenant) => $"catalog:{tenant}";
}
