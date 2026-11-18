using Floruna.SharedKernel;

namespace Floruna.Domain.Catalog;

/// <summary>Taxonomia ("o que é": buquês, arranjos). Diferente de Coleção ("para quê": Dia das Mães). D-019.</summary>
public sealed class Category : AggregateRoot<CategoryId>, ITenantOwned
{
    private Category()
        : base(default)
    {
        Name = string.Empty;
        Slug = null!;
        Description = string.Empty;
    }

    private Category(CategoryId id, TenantId tenantId)
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

    public static Result<Category> Create(TenantId tenantId, string name, string? slug, string? description, bool isActive)
    {
        var category = new Category(CategoryId.New(), tenantId);
        var applied = category.Update(name, slug, description, isActive);
        return applied.IsSuccess ? category : applied.Error;
    }

    public Result Update(string name, string? slug, string? description, bool isActive)
    {
        var parsed = CatalogSlug.FromInputOrName(slug, name);
        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        Name = name.Trim();
        Slug = parsed.Value;
        Description = description?.Trim() ?? string.Empty;
        IsActive = isActive;
        return Result.Success();
    }
}
