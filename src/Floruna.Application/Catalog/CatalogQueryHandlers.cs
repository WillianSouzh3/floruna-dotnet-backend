using System.Globalization;
using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Catalog;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Catalog;

/// <summary>Parâmetros da listagem como chegam na URL (<c>?category=&amp;collection=&amp;search=...</c>).</summary>
public sealed record ProductListRequest(
    string? Category,
    string? Collection,
    string? Search,
    bool OnlyActive,
    bool OnlyFeatured,
    long? MinPrice,
    long? MaxPrice,
    int? Page,
    int? Limit);

/// <summary>Vitrine: sem custo, com cache. <paramref name="Managed"/> = painel/PDV: com custo, sem cache.</summary>
public sealed record ListProductsQuery(ProductListRequest Request, bool Managed) : IQuery<ProductPage>;

internal sealed class ListProductsValidator : AbstractValidator<ListProductsQuery>
{
    public const int MaxPageSize = 100;

    public ListProductsValidator()
    {
        RuleFor(x => x.Request.Page).GreaterThanOrEqualTo(1).When(x => x.Request.Page is not null).WithMessage("page deve ser ≥ 1.");
        RuleFor(x => x.Request.Limit).InclusiveBetween(1, MaxPageSize).When(x => x.Request.Limit is not null)
            .WithMessage($"limit deve estar entre 1 e {MaxPageSize}.");
        RuleFor(x => x.Request.Category).Matches("^[a-z0-9-]{1,60}$").When(x => x.Request.Category is not null).WithMessage("Categoria inválida.");
        RuleFor(x => x.Request.Collection).Matches("^[a-z0-9-]{1,60}$").When(x => x.Request.Collection is not null).WithMessage("Coleção inválida.");
        RuleFor(x => x.Request.MinPrice).GreaterThanOrEqualTo(0).When(x => x.Request.MinPrice is not null);
        RuleFor(x => x.Request.MaxPrice).GreaterThanOrEqualTo(0).When(x => x.Request.MaxPrice is not null);
    }
}

internal sealed class ListProductsHandler(ICatalogQueries queries, ICatalogCache cache)
    : IQueryHandler<ListProductsQuery, ProductPage>
{
    public async Task<Result<ProductPage>> Handle(ListProductsQuery request, CancellationToken cancellationToken)
    {
        var r = request.Request;
        var page = r.Page ?? 1;
        var limit = r.Limit ?? (request.Managed ? 20 : 12);

        if (request.Managed)
        {
            return await LoadAsync(r, publicRead: false, page, limit, cancellationToken);
        }

        // Chave = todos os parâmetros. O cache já separa por loja (ver ICatalogCache).
        var key = string.Join('|', "products", r.Category, r.Collection, r.Search, r.OnlyActive, r.OnlyFeatured, r.MinPrice, r.MaxPrice, page, limit);
        return await cache.GetOrCreateAsync(key, ct => LoadAsync(r, publicRead: true, page, limit, ct), cancellationToken);
    }

    private async Task<ProductPage> LoadAsync(ProductListRequest r, bool publicRead, int page, int limit, CancellationToken cancellationToken)
    {
        Guid? categoryId = null;
        string? legacyCategory = null;
        IReadOnlyList<Guid>? ordered = null;
        var noMatch = false;

        if (r.Category is not null)
        {
            // Categoria dinâmica pelo slug; senão, o enum legado (compatibilidade com o Nest).
            var isLegacy = Enum.TryParse<ProductCategory>(r.Category, ignoreCase: true, out _);
            var category = await queries.FindCategoryBySlugAsync(r.Category, cancellationToken);
            if (category is { } found && (!publicRead || found.IsActive))
            {
                categoryId = found.Id;
            }
            else if (category is null && isLegacy)
            {
                legacyCategory = r.Category;
            }
            else
            {
                noMatch = true;
            }
        }

        if (r.Collection is not null)
        {
            var collection = await queries.FindCollectionBySlugAsync(r.Collection, cancellationToken);
            if (collection is null || (publicRead && !collection.Collection.IsVisibleNow))
            {
                noMatch = true;
            }
            else
            {
                ordered = [.. collection.ProductIds.Select(id => Guid.Parse(id, CultureInfo.InvariantCulture))];
            }
        }

        var filter = new ProductListFilter(r.Search?.Trim(), r.OnlyActive, r.OnlyFeatured, r.MinPrice, r.MaxPrice, categoryId, legacyCategory, ordered, noMatch);
        return await queries.ListProductsAsync(filter, page, limit, includeCost: !publicRead, cancellationToken);
    }
}

public sealed record GetProductBySlugQuery(string Slug) : IQuery<ProductEnvelope>;

internal sealed class GetProductBySlugHandler(ICatalogQueries queries, ICatalogCache cache) : IQueryHandler<GetProductBySlugQuery, ProductEnvelope>
{
    public async Task<Result<ProductEnvelope>> Handle(GetProductBySlugQuery request, CancellationToken cancellationToken)
    {
        var product = await cache.GetOrCreateAsync(
            $"product|{request.Slug}", ct => queries.GetProductBySlugAsync(request.Slug, includeCost: false, ct), cancellationToken);
        return product is null ? CatalogErrors.ProductNotFound : new ProductEnvelope(product);
    }
}

public sealed record ListCategoriesQuery(bool OnlyActive) : IQuery<CategoryList>;

public sealed record GetCategoryQuery(Guid Id) : IQuery<CategoryEnvelope>;

public sealed record ListCollectionsQuery(bool OnlyVisibleNow) : IQuery<CollectionList>;

public sealed record GetCollectionQuery(Guid Id) : IQuery<CollectionWithProductIds>;

public sealed record GetPublicCollectionQuery(string Slug) : IQuery<CollectionWithProducts>;

internal sealed class CategoryCollectionQueryHandlers(ICatalogQueries queries, ICatalogCache cache)
    : IQueryHandler<ListCategoriesQuery, CategoryList>,
      IQueryHandler<GetCategoryQuery, CategoryEnvelope>,
      IQueryHandler<ListCollectionsQuery, CollectionList>,
      IQueryHandler<GetCollectionQuery, CollectionWithProductIds>,
      IQueryHandler<GetPublicCollectionQuery, CollectionWithProducts>
{
    public async Task<Result<CategoryList>> Handle(ListCategoriesQuery request, CancellationToken cancellationToken) =>
        new CategoryList(request.OnlyActive
            ? await cache.GetOrCreateAsync("categories", ct => queries.ListCategoriesAsync(true, ct), cancellationToken)
            : await queries.ListCategoriesAsync(false, cancellationToken));

    public async Task<Result<CategoryEnvelope>> Handle(GetCategoryQuery request, CancellationToken cancellationToken) =>
        await queries.GetCategoryAsync(request.Id, cancellationToken) is { } category
            ? new CategoryEnvelope(category)
            : CatalogErrors.CategoryNotFound;

    public async Task<Result<CollectionList>> Handle(ListCollectionsQuery request, CancellationToken cancellationToken) =>
        new CollectionList(await queries.ListCollectionsAsync(request.OnlyVisibleNow, cancellationToken));

    public async Task<Result<CollectionWithProductIds>> Handle(GetCollectionQuery request, CancellationToken cancellationToken) =>
        await queries.GetCollectionAsync(request.Id, cancellationToken) is { } collection
            ? collection
            : CatalogErrors.CollectionNotFound;

    public async Task<Result<CollectionWithProducts>> Handle(GetPublicCollectionQuery request, CancellationToken cancellationToken)
    {
        var collection = await queries.FindCollectionBySlugAsync(request.Slug, cancellationToken);
        if (collection is null || !collection.Collection.IsVisibleNow)
        {
            return CatalogErrors.CollectionNotFound;
        }

        var ids = collection.ProductIds.Select(id => Guid.Parse(id, CultureInfo.InvariantCulture)).ToList();
        var products = await queries.GetProductsInOrderAsync(ids, onlyActive: true, cancellationToken);
        return new CollectionWithProducts(collection.Collection, products);
    }
}
