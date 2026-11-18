namespace Floruna.Application.Catalog;

/// <summary>
/// Lado de LEITURA do CQRS: consultas que devolvem DTOs prontos, projetados
/// direto no SQL (sem carregar agregados nem rastrear mudanças). A
/// implementação mora na Infrastructure; o handler só orquestra.
/// </summary>
public interface ICatalogQueries
{
    Task<ProductPage> ListProductsAsync(ProductListFilter filter, int page, int limit, bool includeCost, CancellationToken cancellationToken);

    Task<ProductResponse?> GetProductBySlugAsync(string slug, bool includeCost, CancellationToken cancellationToken);

    Task<IReadOnlyList<CategoryResponse>> ListCategoriesAsync(bool onlyActive, CancellationToken cancellationToken);

    Task<CategoryResponse?> GetCategoryAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Categoria por slug (para resolver o filtro <c>?category=</c>).</summary>
    Task<(Guid Id, bool IsActive)?> FindCategoryBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<IReadOnlyList<CollectionResponse>> ListCollectionsAsync(bool onlyVisibleNow, CancellationToken cancellationToken);

    Task<CollectionWithProductIds?> GetCollectionAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Coleção por slug com os IDs dos produtos (para a vitrine e o filtro <c>?collection=</c>).</summary>
    Task<CollectionWithProductIds?> FindCollectionBySlugAsync(string slug, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductResponse>> GetProductsInOrderAsync(IReadOnlyList<Guid> ids, bool onlyActive, CancellationToken cancellationToken);
}

/// <summary>
/// Cache-aside da vitrine (padrão de nuvem). As chaves são SEMPRE por loja
/// (senão a loja A veria o catálogo da B) e cada escrita no catálogo invalida
/// tudo daquela loja por TAG.
/// </summary>
public interface ICatalogCache
{
    Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken);

    Task InvalidateAsync(CancellationToken cancellationToken);
}
