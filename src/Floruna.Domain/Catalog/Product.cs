using Floruna.SharedKernel;

namespace Floruna.Domain.Catalog;

/// <summary>Categoria fixa legada do Nest (ainda obrigatória no contrato; as dinâmicas ficam em <see cref="Category"/>).</summary>
public enum ProductCategory
{
    Romance,
    Buques,
    Eventos,
    Presentes,
    Cartoes,
}

/// <summary>Dados de entrada de um produto (o que o lojista preenche no formulário).</summary>
public sealed record ProductDraft(
    string Name,
    string? Slug,
    string ShortDescription,
    string Description,
    ProductCategory Category,
    IReadOnlyList<CategoryId> CategoryIds,
    long PriceCents,
    long? DiscountPriceCents,
    long? CostCents,
    IReadOnlyList<string> Images,
    int Stock,
    int LowStockThreshold,
    bool IsActive,
    bool IsFeatured,
    string? Sku,
    string? Barcode,
    IReadOnlyList<VariantDraft> Variants);

/// <param name="Id">Ausente = variação nova (ou casamento pelo nome com uma existente).</param>
/// <param name="PriceCents">Preço FINAL da variação.</param>
public sealed record VariantDraft(string? Id, string? Sku, string Label, long PriceCents, bool? IsActive);

/// <summary>Unidade comprável de um produto (tamanho, cor). Entidade dentro do agregado Product.</summary>
public sealed class ProductVariant
{
    private ProductVariant()
    {
        Id = default;
        Label = string.Empty;
    }

    internal ProductVariant(VariantId id, string? sku, string label, Money price, bool isActive)
    {
        Id = id;
        Sku = sku;
        Label = label;
        PriceCents = price.Cents;
        IsActive = isActive;
    }

    public VariantId Id { get; private set; }

    public string? Sku { get; private set; }

    public string Label { get; private set; }

    /// <summary>Persistido em centavos (coluna numérica filtrável no SQL); <see cref="Price"/> é a visão de domínio.</summary>
    public long PriceCents { get; private set; }

    public Money Price => Money.FromCents(PriceCents);

    public bool IsActive { get; private set; }
}

public sealed class ProductCategoryLink
{
    private ProductCategoryLink()
    {
    }

    internal ProductCategoryLink(CategoryId categoryId) => CategoryId = categoryId;

    public CategoryId CategoryId { get; private set; }
}

/// <summary>
/// Produto do catálogo. Raiz de agregado: as variações só mudam por aqui,
/// então as regras entre elas (nomes/SKUs únicos, IDs estáveis) ficam num lugar só.
/// </summary>
/// <remarks>
/// <see cref="Stock"/> é TRANSITÓRIO: na Fase 3 a disponibilidade passa a vir do
/// estoque (lotes + ledger) e este campo vira só o saldo inicial migrado
/// (Branch by Abstraction, como a nota 028 do Nest).
/// </remarks>
public sealed class Product : AggregateRoot<ProductId>, ITenantOwned
{
    public const int MaxVariants = 12;
    public const int MaxCategories = 8;

    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductCategoryLink> _categories = [];

    private Product()
        : base(default)
    {
        Name = string.Empty;
        Slug = null!;
        ShortDescription = string.Empty;
        Description = string.Empty;
        Images = [];
        Sku = string.Empty;
        Barcode = string.Empty;
    }

    private Product(ProductId id, TenantId tenantId, DateTimeOffset now)
        : base(id)
    {
        Name = string.Empty;
        Slug = null!;
        ShortDescription = string.Empty;
        Description = string.Empty;
        Images = [];
        Sku = string.Empty;
        Barcode = string.Empty;
        TenantId = tenantId;
        CreatedAt = now;
    }

    public TenantId TenantId { get; private set; }

    public string Sku { get; private set; }

    public string Barcode { get; private set; }

    public CatalogSlug Slug { get; private set; }

    public string Name { get; private set; }

    public string ShortDescription { get; private set; }

    public string Description { get; private set; }

    public ProductCategory Category { get; private set; }

    // Dinheiro persistido em centavos: colunas numéricas que o SQL consegue filtrar
    // e ordenar ("preço efetivo entre X e Y"). As propriedades Money são a visão de domínio.
    public long PriceCents { get; private set; }

    public long? DiscountPriceCents { get; private set; }

    /// <summary>Custo corrente (margem). Nunca exposto na vitrine.</summary>
    public long? CostCents { get; private set; }

    public Money Price => Money.FromCents(PriceCents);

    public Money? DiscountPrice => DiscountPriceCents is { } d ? Money.FromCents(d) : null;

    public Money? Cost => CostCents is { } c ? Money.FromCents(c) : null;

    public List<string> Images { get; private set; }

    public int Stock { get; private set; }

    public int LowStockThreshold { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsFeatured { get; private set; }

    public IReadOnlyList<ProductVariant> Variants => _variants.AsReadOnly();

    public IReadOnlyList<ProductCategoryLink> Categories => _categories.AsReadOnly();

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>O que o cliente paga hoje: o promocional, se houver.</summary>
    public Money EffectivePrice => DiscountPrice ?? Price;

    public static Result<Product> Create(TenantId tenantId, ProductDraft draft, DateTimeOffset now)
    {
        var product = new Product(ProductId.New(), tenantId, now);
        var applied = product.Apply(draft, now);
        return applied.IsSuccess ? product : applied.Error;
    }

    public Result Update(ProductDraft draft, DateTimeOffset now) => Apply(draft, now);

    private Result Apply(ProductDraft draft, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var slug = CatalogSlug.FromInputOrName(draft.Slug, draft.Name);
        if (slug.IsFailure)
        {
            return slug.Error;
        }

        if (draft.DiscountPriceCents is { } discount && discount >= draft.PriceCents)
        {
            return CatalogErrors.DiscountNotBelowPrice; // RN-11
        }

        if (draft.Images.Count == 0)
        {
            return CatalogErrors.ImagesRequired;
        }

        if (draft.CategoryIds.Distinct().Count() != draft.CategoryIds.Count)
        {
            return CatalogErrors.DuplicateCategories;
        }

        var variants = ReconcileVariants(draft.Variants);
        if (variants.IsFailure)
        {
            return variants.Error;
        }

        Name = draft.Name.Trim();
        Slug = slug.Value;
        ShortDescription = draft.ShortDescription.Trim();
        Description = draft.Description.Trim();
        Category = draft.Category;
        PriceCents = Money.FromCents(draft.PriceCents).Cents;
        DiscountPriceCents = draft.DiscountPriceCents is { } d ? Money.FromCents(d).Cents : null;
        CostCents = draft.CostCents is { } c ? Money.FromCents(c).Cents : null;
        Images = [.. draft.Images];
        Stock = draft.Stock;
        LowStockThreshold = draft.LowStockThreshold;
        IsActive = draft.IsActive;
        IsFeatured = draft.IsFeatured;
        Sku = draft.Sku?.Trim().ToUpperInvariant() ?? string.Empty;
        Barcode = draft.Barcode?.Trim() ?? string.Empty;

        _categories.Clear();
        _categories.AddRange(draft.CategoryIds.Select(id => new ProductCategoryLink(id)));
        _variants.Clear();
        _variants.AddRange(variants.Value);
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Casa as variações recebidas com as atuais para PRESERVAR os IDs (mesma lógica
    /// do <c>prepareVariants</c> do Nest): pelo ID, ou pelo nome quando o ID não vem.
    /// Uma variação "M" renomeada para "Médio" mantém o mesmo ID se o ID for enviado.
    /// </summary>
    private Result<List<ProductVariant>> ReconcileVariants(IReadOnlyList<VariantDraft> incoming)
    {
        if (incoming.Count > MaxVariants)
        {
            return CatalogErrors.TooManyVariants;
        }

        var byId = _variants.ToDictionary(v => v.Id.Value, StringComparer.Ordinal);
        var byLabel = _variants.GroupBy(v => v.Label.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ProductVariant>(incoming.Count);

        foreach (var input in incoming)
        {
            ProductVariant? existing = null;
            if (input.Id is not null)
            {
                if (!VariantId.IsValid(input.Id))
                {
                    return Error.Validation("VALIDATION_ERROR", "ID de variação inválido.", ["ID de variação inválido."]);
                }

                if (_variants.Count > 0 && !byId.TryGetValue(input.Id, out existing))
                {
                    return CatalogErrors.VariantNotInProduct(input.Label);
                }
            }
            else if (byLabel.TryGetValue(input.Label.Trim().ToLowerInvariant(), out var match) && !claimed.Contains(match.Id.Value))
            {
                existing = match;
            }

            var id = existing?.Id ?? (input.Id is not null ? new VariantId(input.Id) : VariantId.New());
            claimed.Add(id.Value);
            var sku = (input.Sku ?? existing?.Sku)?.Trim().ToUpperInvariant();
            result.Add(new ProductVariant(
                id,
                string.IsNullOrEmpty(sku) ? null : sku,
                input.Label.Trim(),
                Money.FromCents(input.PriceCents),
                input.IsActive ?? existing?.IsActive ?? true));
        }

        if (result.Select(v => v.Label.ToLowerInvariant()).Distinct().Count() != result.Count)
        {
            return CatalogErrors.DuplicateVariantLabel;
        }

        if (result.Select(v => v.Id).Distinct().Count() != result.Count)
        {
            return CatalogErrors.DuplicateVariantId;
        }

        var skus = result.Where(v => v.Sku is not null).Select(v => v.Sku).ToList();
        return skus.Distinct(StringComparer.Ordinal).Count() != skus.Count ? CatalogErrors.DuplicateVariantSku : result;
    }
}
