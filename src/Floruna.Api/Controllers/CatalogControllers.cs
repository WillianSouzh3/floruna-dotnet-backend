using Floruna.Application.Catalog;
using Floruna.Application.Messaging;
using Floruna.Domain.Catalog;
using Floruna.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Floruna.Api.Controllers;

public sealed record VariantRequest(string? Id, string? Sku, string? Label, long? Price, bool? IsActive);

/// <summary>Corpo de criação/atualização. Em PUT, campos ausentes não mudam (igual ao PartialType do Nest).</summary>
public sealed record ProductRequest(
    string? Name,
    string? Slug,
    string? ShortDescription,
    string? Description,
    string? Category,
    IReadOnlyList<Guid>? CategoryIds,
    long? Price,
    Optional<long?> DiscountPrice,
    Optional<long?> Cost,
    IReadOnlyList<string>? Images,
    int? Stock,
    int? LowStockThreshold,
    bool? IsActive,
    bool? IsFeatured,
    string? Sku,
    string? Barcode,
    IReadOnlyList<VariantRequest>? Variants)
{
    public ProductInput ToInput() => new(
        Name, Slug, ShortDescription, Description, Category, CategoryIds, Price, DiscountPrice, Cost, Images, Stock,
        LowStockThreshold, IsActive, IsFeatured, Sku, Barcode,
        Variants?.Select(v => new VariantDraft(v.Id, v.Sku, v.Label ?? string.Empty, v.Price ?? 0, v.IsActive)).ToList());
}

/// <summary>Filtros da URL. <c>onlyActive</c>/<c>onlyFeatured</c> chegam como texto ("true"), como no Nest.</summary>
public sealed record ProductQueryParameters(
    string? Category, string? Collection, string? Search, string? OnlyActive, string? OnlyFeatured,
    long? MinPrice, long? MaxPrice, int? Page, int? Limit)
{
    public ProductListRequest ToRequest() => new(
        Category, Collection, Search, OnlyActive == "true", OnlyFeatured == "true", MinPrice, MaxPrice, Page, Limit);
}

/// <summary>
/// Catálogo de produtos. Leitura pública (vitrine, com cache), escrita só do admin.
/// Repare: o controller não sabe NADA de EF, cache ou plano: só monta a mensagem e devolve o resultado.
/// </summary>
[Route(Contracts.ApiVersion.RoutePrefix + "/products")]
public sealed class ProductsController(ISender sender) : ApiController
{
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<ProductPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] ProductQueryParameters query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FromResult(await sender.Send(new ListProductsQuery(query.ToRequest(), Managed: false), cancellationToken), Ok);
    }

    /// <summary>Painel/PDV: inclui o custo (margem) e não usa cache.</summary>
    [HttpGet("manage")]
    [Authorize(Roles = Roles.PointOfSale)]
    [ProducesResponseType<ProductPage>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListManaged([FromQuery] ProductQueryParameters query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FromResult(await sender.Send(new ListProductsQuery(query.ToRequest(), Managed: true), cancellationToken), Ok);
    }

    [HttpGet("{slug}")]
    [AllowAnonymous]
    [ProducesResponseType<ProductEnvelope>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBySlug(string slug, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetProductBySlugQuery(slug), cancellationToken), Ok);

    [HttpPost]
    [Authorize(Roles = Roles.Admins)]
    [ProducesResponseType<ProductEnvelope>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(ProductRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new CreateProductCommand(body.ToInput()), cancellationToken);
        return FromResult(result, product => StatusCode(StatusCodes.Status201Created, product));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    [ProducesResponseType<ProductEnvelope>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, ProductRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new UpdateProductCommand(id, body.ToInput()), cancellationToken), Ok);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteProductCommand(id), cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : Failure(result.Error);
    }
}

public sealed record CategoryRequest(string? Name, string? Slug, string? Description, bool? IsActive)
{
    public CategoryInput ToInput() => new(Name, Slug, Description, IsActive);
}

[Route(Contracts.ApiVersion.RoutePrefix + "/categories")]
public sealed class CategoriesController(ISender sender) : ApiController
{
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<CategoryList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPublic(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ListCategoriesQuery(OnlyActive: true), cancellationToken), Ok);

    [HttpGet("manage")]
    [Authorize(Roles = Roles.Admins)]
    [ProducesResponseType<CategoryList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAll(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ListCategoriesQuery(OnlyActive: false), cancellationToken), Ok);

    [HttpGet("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetCategoryQuery(id), cancellationToken), Ok);

    [HttpPost]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Create(CategoryRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new CreateCategoryCommand(body.ToInput()), cancellationToken);
        return FromResult(result, category => StatusCode(StatusCodes.Status201Created, category));
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Update(Guid id, CategoryRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new UpdateCategoryCommand(id, body.ToInput()), cancellationToken), Ok);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteCategoryCommand(id), cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : Failure(result.Error);
    }
}

public sealed record CollectionRequest(
    string? Name, string? Slug, string? Description, bool? IsActive, Optional<DateTimeOffset?> StartsAt, Optional<DateTimeOffset?> EndsAt)
{
    public CollectionInput ToInput() => new(Name, Slug, Description, IsActive, StartsAt, EndsAt);
}

public sealed record ReplaceCollectionProductsRequest(IReadOnlyList<Guid>? ProductIds);

[Route(Contracts.ApiVersion.RoutePrefix + "/collections")]
public sealed class CollectionsController(ISender sender) : ApiController
{
    /// <summary>Coleções visíveis AGORA (ativas e dentro da janela de datas).</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> ListPublic(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ListCollectionsQuery(OnlyVisibleNow: true), cancellationToken), Ok);

    [HttpGet("slug/{slug}")]
    [AllowAnonymous]
    [ProducesResponseType<CollectionWithProducts>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPublic(string slug, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetPublicCollectionQuery(slug), cancellationToken), Ok);

    [HttpGet("manage")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> ListAll(CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new ListCollectionsQuery(OnlyVisibleNow: false), cancellationToken), Ok);

    [HttpGet("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    [ProducesResponseType<CollectionWithProductIds>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        FromResult(await sender.Send(new GetCollectionQuery(id), cancellationToken), Ok);

    [HttpPost]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Create(CollectionRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await sender.Send(new CreateCollectionCommand(body.ToInput()), cancellationToken);
        return FromResult(result, collection => StatusCode(StatusCodes.Status201Created, collection));
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Update(Guid id, CollectionRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new UpdateCollectionCommand(id, body.ToInput()), cancellationToken), Ok);
    }

    /// <summary>Define os produtos da coleção, na ordem de exibição.</summary>
    [HttpPut("{id:guid}/products")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> ReplaceProducts(Guid id, ReplaceCollectionProductsRequest body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return FromResult(await sender.Send(new ReplaceCollectionProductsCommand(id, body.ProductIds ?? []), cancellationToken), Ok);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Admins)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteCollectionCommand(id), cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : Failure(result.Error);
    }
}
