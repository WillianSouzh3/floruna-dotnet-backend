using System.Text.Json.Serialization;

namespace Floruna.Application.Catalog;

// Formato idêntico ao toJSON do Nest (produto + availability).

public sealed record VariantResponse(
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sku,
    string Label,
    long Price,
    bool IsActive,
    string InventoryPolicy,
    string ProductionPolicy);

/// <summary>Disponibilidade. Na Fase 2 vem do campo do produto (<c>source: "product"</c>); na Fase 3, do estoque.</summary>
public sealed record AvailabilityResponse(int OnHand, int Reserved, int Available, string Source);

public sealed record ProductResponse(
    string Id,
    string Sku,
    string Barcode,
    string Slug,
    string Name,
    string ShortDescription,
    string Description,
    string Category,
    IReadOnlyList<string> CategoryIds,
    long Price,
    long? DiscountPrice,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Cost,
    IReadOnlyList<string> Images,
    int Stock,
    int LowStockThreshold,
    bool IsActive,
    bool IsFeatured,
    IReadOnlyList<VariantResponse> Variants,
    string CreatedAt,
    string UpdatedAt,
    AvailabilityResponse Availability)
{
    // Campos fiscais do contrato: ficam vazios até o módulo fiscal (backlog).
    public string Ncm => string.Empty;

    public string Cest => string.Empty;

    public string Origem => string.Empty;

    public string Cfop => string.Empty;

    public string Csosn => string.Empty;

    public string Cst => string.Empty;

    public string Unidade => "UN";
}

public sealed record ProductPage(IReadOnlyList<ProductResponse> Products, int Total, int Page, int Limit, int Pages);

public sealed record ProductEnvelope(ProductResponse Product);

public sealed record CategoryResponse(string Id, string Name, string Slug, string Description, bool IsActive);

public sealed record CategoryList(IReadOnlyList<CategoryResponse> Categories);

public sealed record CategoryEnvelope(CategoryResponse Category);

public sealed record CollectionResponse(
    string Id, string Name, string Slug, string Description, bool IsActive, string? StartsAt, string? EndsAt, string State, bool IsVisibleNow);

public sealed record CollectionList(IReadOnlyList<CollectionResponse> Collections);

public sealed record CollectionEnvelope(CollectionResponse Collection);

/// <summary>Detalhe no painel: a coleção + os IDs dos produtos, na ordem.</summary>
public sealed record CollectionWithProductIds(CollectionResponse Collection, IReadOnlyList<string> ProductIds);

/// <summary>Coleção pública: a coleção + os produtos ativos, na ordem da curadoria.</summary>
public sealed record CollectionWithProducts(CollectionResponse Collection, IReadOnlyList<ProductResponse> Products);

/// <summary>Filtro da listagem já RESOLVIDO (slugs de categoria/coleção viram IDs).</summary>
public sealed record ProductListFilter(
    string? Search,
    bool OnlyActive,
    bool OnlyFeatured,
    long? MinPrice,
    long? MaxPrice,
    Guid? CategoryId,
    string? LegacyCategory,
    IReadOnlyList<Guid>? OrderedProductIds,
    bool NoMatch);
