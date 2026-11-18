using Floruna.Application.Catalog;
using Floruna.Domain.Catalog;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Catalog;

/// <summary>
/// Lado de LEITURA do CQRS. <c>AsNoTracking</c>: o EF não monta o rastreamento de
/// mudanças (mais rápido e menos memória), porque ninguém vai alterar estes objetos.
/// O filtro da loja continua valendo (query filter global).
/// </summary>
internal sealed class CatalogQueries(FlorunaDbContext db, TimeProvider clock) : ICatalogQueries
{
    public async Task<ProductPage> ListProductsAsync(ProductListFilter filter, int page, int limit, bool includeCost, CancellationToken cancellationToken)
    {
        if (filter.NoMatch)
        {
            return new ProductPage([], 0, page, limit, 0);
        }

        var query = Filtered(filter);

        // Coleção: a ORDEM é a da curadoria (até 100 itens), então ordena e pagina em memória.
        if (filter.OrderedProductIds is { } ordered)
        {
            var all = await query.ToListAsync(cancellationToken);
            var position = ordered.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
            var sorted = all.OrderBy(p => position.GetValueOrDefault(p.Id.Value, int.MaxValue)).ToList();
            return Page(sorted.Skip((page - 1) * limit).Take(limit).ToList(), sorted.Count, page, limit, includeCost);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * limit).Take(limit)
            .ToListAsync(cancellationToken);
        return Page(items, total, page, limit, includeCost);
    }

    public async Task<ProductResponse?> GetProductBySlugAsync(string slug, bool includeCost, CancellationToken cancellationToken)
    {
        var target = CatalogSlug.FromPersistence(slug);
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == target, cancellationToken);
        return product is null ? null : ToResponse(product, includeCost);
    }

    public async Task<IReadOnlyList<CategoryResponse>> ListCategoriesAsync(bool onlyActive, CancellationToken cancellationToken) =>
        (await db.Categories.AsNoTracking().Where(c => !onlyActive || c.IsActive).OrderBy(c => c.Name).ToListAsync(cancellationToken))
            .Select(CatalogMapping.ToResponse).ToList();

    public async Task<CategoryResponse?> GetCategoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == new CategoryId(id), cancellationToken);
        return category is null ? null : CatalogMapping.ToResponse(category);
    }

    public async Task<(Guid Id, bool IsActive)?> FindCategoryBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        var target = CatalogSlug.FromPersistence(slug);
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == target, cancellationToken);
        return category is null ? null : (category.Id.Value, category.IsActive);
    }

    public async Task<IReadOnlyList<CollectionResponse>> ListCollectionsAsync(bool onlyVisibleNow, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var collections = await db.Collections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken);
        return collections.Where(c => !onlyVisibleNow || c.IsVisibleAt(now)).Select(c => CatalogMapping.ToResponse(c, now)).ToList();
    }

    public async Task<CollectionWithProductIds?> GetCollectionAsync(Guid id, CancellationToken cancellationToken) =>
        WithIds(await db.Collections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == new CollectionId(id), cancellationToken));

    public async Task<CollectionWithProductIds?> FindCollectionBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        var target = CatalogSlug.FromPersistence(slug);
        return WithIds(await db.Collections.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == target, cancellationToken));
    }

    public async Task<IReadOnlyList<ProductResponse>> GetProductsInOrderAsync(IReadOnlyList<Guid> ids, bool onlyActive, CancellationToken cancellationToken)
    {
        var page = await ListProductsAsync(
            new ProductListFilter(null, onlyActive, false, null, null, null, null, ids, false), 1, Collection.MaxProducts, false, cancellationToken);
        return page.Products;
    }

    private IQueryable<Product> Filtered(ProductListFilter filter)
    {
        var query = db.Products.AsNoTracking();

        if (filter.OnlyActive)
        {
            query = query.Where(p => p.IsActive);
        }

        if (filter.OnlyFeatured)
        {
            query = query.Where(p => p.IsFeatured);
        }

        if (filter.CategoryId is { } categoryId)
        {
            var target = new CategoryId(categoryId);
            query = query.Where(p => p.Categories.Any(c => c.CategoryId == target));
        }
        else if (filter.LegacyCategory is { } legacy)
        {
            var category = Enum.Parse<ProductCategory>(legacy, ignoreCase: true);
            query = query.Where(p => p.Category == category);
        }

        if (filter.OrderedProductIds is { } ids)
        {
            var targets = ids.Select(id => new ProductId(id)).ToList();
            query = query.Where(p => targets.Contains(p.Id));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // ILIKE com curingas escapados. Busca acentuada/por relevância: pg_trgm ou tsvector (evolução).
            var pattern = $"%{filter.Search.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal)}%";
            query = query.Where(p => EF.Functions.ILike(p.Name, pattern) || EF.Functions.ILike(p.Description, pattern));
        }

        // Faixa de preço sobre o preço EFETIVO (promocional, se houver), como no Nest.
        if (filter.MinPrice is { } min)
        {
            query = query.Where(p => (p.DiscountPriceCents ?? p.PriceCents) >= min);
        }

        if (filter.MaxPrice is { } max)
        {
            query = query.Where(p => (p.DiscountPriceCents ?? p.PriceCents) <= max);
        }

        return query;
    }

    private static ProductPage Page(List<Product> items, int total, int page, int limit, bool includeCost) =>
        new([.. items.Select(p => ToResponse(p, includeCost))], total, page, limit, (int)Math.Ceiling(total / (double)limit));

    private CollectionWithProductIds? WithIds(Collection? collection) => collection is null
        ? null
        : new CollectionWithProductIds(
            CatalogMapping.ToResponse(collection, clock.GetUtcNow()),
            [.. collection.Items.OrderBy(i => i.Position).Select(i => i.ProductId.ToString())]);

    private static ProductResponse ToResponse(Product p, bool includeCost) => new(
        p.Id.ToString(),
        p.Sku,
        p.Barcode,
        p.Slug.Value,
        p.Name,
        p.ShortDescription,
        p.Description,
        p.Category.ToString().ToLowerInvariant(),
        [.. p.Categories.Select(c => c.CategoryId.ToString())],
        p.PriceCents,
        p.DiscountPriceCents,
        includeCost ? p.CostCents : null,
        [.. p.Images],
        p.Stock,
        p.LowStockThreshold,
        p.IsActive,
        p.IsFeatured,
        [.. p.Variants.Select(v => new VariantResponse(v.Id.Value, v.Sku, v.Label, v.PriceCents, v.IsActive, "shared_product", "finished_good"))],
        CatalogMapping.Iso(p.CreatedAt),
        CatalogMapping.Iso(p.UpdatedAt),
        new AvailabilityResponse(p.Stock, 0, p.Stock, "product"));
}
