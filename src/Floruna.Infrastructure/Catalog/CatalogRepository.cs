using Floruna.Domain.Catalog;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Floruna.Infrastructure.Catalog;

/// <summary>Lado de ESCRITA: carrega agregados inteiros (com variações/itens) para o domínio alterar.</summary>
internal sealed class CatalogRepository(FlorunaDbContext db) : ICatalogRepository
{
    public void Add(Product product) => db.Products.Add(product);

    public void Add(Category category) => db.Categories.Add(category);

    public void Add(Collection collection) => db.Collections.Add(collection);

    public void Remove(Product product) => db.Products.Remove(product);

    public void Remove(Category category) => db.Categories.Remove(category);

    public void Remove(Collection collection) => db.Collections.Remove(collection);

    public Task<Product?> GetProductAsync(ProductId id, CancellationToken cancellationToken) =>
        db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Category?> GetCategoryAsync(CategoryId id, CancellationToken cancellationToken) =>
        db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<Collection?> GetCollectionAsync(CollectionId id, CancellationToken cancellationToken) =>
        db.Collections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<int> CountProductsAsync(CancellationToken cancellationToken) => db.Products.CountAsync(cancellationToken);

    public Task<int> CountActiveCategoriesAsync(IReadOnlyCollection<CategoryId> ids, CancellationToken cancellationToken) =>
        db.Categories.CountAsync(c => ids.Contains(c.Id) && c.IsActive, cancellationToken);

    public Task<int> CountExistingProductsAsync(IReadOnlyCollection<ProductId> ids, CancellationToken cancellationToken) =>
        db.Products.CountAsync(p => ids.Contains(p.Id), cancellationToken);

    public Task<bool> CategoryHasProductsAsync(CategoryId id, CancellationToken cancellationToken) =>
        db.Products.AnyAsync(p => p.Categories.Any(c => c.CategoryId == id), cancellationToken);

    public async Task<CategoryId?> FindActiveCategoryBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        var target = CatalogSlug.FromPersistence(slug);
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == target && c.IsActive, cancellationToken);
        return category?.Id;
    }

    public Task<bool> AnyVariantSkuTakenAsync(IReadOnlyCollection<string> skus, ProductId? except, CancellationToken cancellationToken) =>
        db.Products
            .Where(p => except == null || p.Id != except)
            .SelectMany(p => p.Variants)
            .AnyAsync(v => v.Sku != null && skus.Contains(v.Sku), cancellationToken);
}
