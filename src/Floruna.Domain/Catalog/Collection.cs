using Floruna.SharedKernel;

namespace Floruna.Domain.Catalog;

public enum CollectionState
{
    Inactive,
    Scheduled,
    Active,
    Ended,
}

public sealed class CollectionItem
{
    private CollectionItem()
    {
    }

    internal CollectionItem(ProductId productId, int position)
    {
        ProductId = productId;
        Position = position;
    }

    public ProductId ProductId { get; private set; }

    /// <summary>Ordem de exibição definida pelo lojista (curadoria).</summary>
    public int Position { get; private set; }
}

/// <summary>
/// Curadoria com janela de tempo ("Dia das Mães", de 01/05 a 12/05). Fora da
/// janela, some da vitrine sozinha, sem ninguém precisar desligar.
/// </summary>
public sealed class Collection : AggregateRoot<CollectionId>, ITenantOwned
{
    public const int MaxProducts = 100;

    private readonly List<CollectionItem> _items = [];

    private Collection()
        : base(default)
    {
        Name = string.Empty;
        Slug = null!;
        Description = string.Empty;
    }

    private Collection(CollectionId id, TenantId tenantId)
        : base(id)
    {
        TenantId = tenantId;
        Name = string.Empty;
        Slug = null!;
        Description = string.Empty;
    }

    public TenantId TenantId { get; private set; }

    public string Name { get; private set; }

    public CatalogSlug Slug { get; private set; }

    public string Description { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset? StartsAt { get; private set; }

    public DateTimeOffset? EndsAt { get; private set; }

    public IReadOnlyList<CollectionItem> Items => _items.AsReadOnly();

    public static Result<Collection> Create(TenantId tenantId, string name, string? slug, string? description, bool isActive, DateTimeOffset? startsAt, DateTimeOffset? endsAt)
    {
        var collection = new Collection(CollectionId.New(), tenantId);
        var applied = collection.Update(name, slug, description, isActive, startsAt, endsAt);
        return applied.IsSuccess ? collection : applied.Error;
    }

    public Result Update(string name, string? slug, string? description, bool isActive, DateTimeOffset? startsAt, DateTimeOffset? endsAt)
    {
        var parsed = CatalogSlug.FromInputOrName(slug, name);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        if (startsAt is not null && endsAt is not null && endsAt <= startsAt)
        {
            return CatalogErrors.CollectionWindowInvalid;
        }

        Name = name.Trim();
        Slug = parsed.Value;
        Description = description?.Trim() ?? string.Empty;
        IsActive = isActive;
        StartsAt = startsAt;
        EndsAt = endsAt;
        return Result.Success();
    }

    public CollectionState StateAt(DateTimeOffset now) => this switch
    {
        { IsActive: false } => CollectionState.Inactive,
        { StartsAt: { } start } when now < start => CollectionState.Scheduled,
        { EndsAt: { } end } when now > end => CollectionState.Ended,
        _ => CollectionState.Active,
    };

    public bool IsVisibleAt(DateTimeOffset now) => StateAt(now) == CollectionState.Active;

    /// <summary>Substitui os produtos, na ordem recebida. Quem chama garante que existem na loja.</summary>
    public Result ReplaceProducts(IReadOnlyList<ProductId> productIds)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        if (productIds.Count > MaxProducts)
        {
            return Error.Validation("VALIDATION_ERROR", "Uma coleção pode ter no máximo 100 produtos.");
        }

        if (productIds.Distinct().Count() != productIds.Count)
        {
            return CatalogErrors.DuplicateCollectionProducts;
        }

        _items.Clear();
        _items.AddRange(productIds.Select((id, index) => new CollectionItem(id, index)));
        return Result.Success();
    }
}

public interface ICatalogRepository
{
    void Add(Product product);

    void Add(Category category);

    void Add(Collection collection);

    void Remove(Product product);

    void Remove(Category category);

    void Remove(Collection collection);

    Task<Product?> GetProductAsync(ProductId id, CancellationToken cancellationToken);

    Task<Category?> GetCategoryAsync(CategoryId id, CancellationToken cancellationToken);

    Task<Collection?> GetCollectionAsync(CollectionId id, CancellationToken cancellationToken);

    Task<int> CountProductsAsync(CancellationToken cancellationToken);

    Task<int> CountActiveCategoriesAsync(IReadOnlyCollection<CategoryId> ids, CancellationToken cancellationToken);

    Task<int> CountExistingProductsAsync(IReadOnlyCollection<ProductId> ids, CancellationToken cancellationToken);

    Task<bool> CategoryHasProductsAsync(CategoryId id, CancellationToken cancellationToken);

    /// <summary>Categoria ATIVA com este slug (mapeia a categoria legada do enum para a dinâmica).</summary>
    Task<CategoryId?> FindActiveCategoryBySlugAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Algum OUTRO produto da loja usa um destes SKUs de variação?</summary>
    Task<bool> AnyVariantSkuTakenAsync(IReadOnlyCollection<string> skus, ProductId? except, CancellationToken cancellationToken);
}
