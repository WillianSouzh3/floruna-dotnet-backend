using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Catalog;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Catalog;

/// <summary>Campos do produto vindos do formulário. Na atualização, null = "não alterar" (PATCH, como no Nest).</summary>
public sealed record ProductInput(
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
    IReadOnlyList<VariantDraft>? Variants);

/// <summary>
/// Diferencia "campo ausente" de "campo enviado como null". Em <c>discountPrice</c>,
/// ausente mantém o promocional; <c>null</c> explícito REMOVE o promocional.
/// </summary>
public readonly record struct Optional<T>(bool HasValue, T Value)
{
    public static Optional<T> Absent => default;

    public static Optional<T> Of(T value) => new(true, value);

    public T Or(T fallback) => HasValue ? Value : fallback;
}

internal sealed class ProductInputValidator : AbstractValidator<ProductInput>
{
    public ProductInputValidator(bool creating)
    {
        RuleFor(x => x.Name).NotEmpty().Length(2, 120).When(x => creating || x.Name is not null)
            .WithMessage("O nome deve ter entre 2 e 120 caracteres.");
        RuleFor(x => x.ShortDescription).NotEmpty().Length(10, 200).When(x => creating || x.ShortDescription is not null)
            .WithMessage("A descrição curta deve ter entre 10 e 200 caracteres.");
        RuleFor(x => x.Description).NotEmpty().MinimumLength(10).When(x => creating || x.Description is not null)
            .WithMessage("A descrição deve ter ao menos 10 caracteres.");
        RuleFor(x => x.Category).NotEmpty().Must(c => Enum.TryParse<ProductCategory>(c, ignoreCase: true, out _))
            .When(x => creating || x.Category is not null).WithMessage("Categoria inválida.");
        RuleFor(x => x.Price).NotNull().GreaterThanOrEqualTo(1).When(x => creating || x.Price is not null)
            .WithMessage("O preço deve ser de ao menos 1 centavo.");
        RuleFor(x => x.DiscountPrice.Value).GreaterThanOrEqualTo(1).When(x => x.DiscountPrice is { HasValue: true, Value: not null })
            .WithMessage("O preço promocional deve ser de ao menos 1 centavo.");
        RuleFor(x => x.Cost.Value).GreaterThanOrEqualTo(0).When(x => x.Cost is { HasValue: true, Value: not null })
            .WithMessage("O custo não pode ser negativo.");
        RuleFor(x => x.Images).NotEmpty().When(x => creating || x.Images is not null).WithMessage("Informe ao menos uma imagem.");
        RuleFor(x => x.Stock).NotNull().GreaterThanOrEqualTo(0).When(x => creating || x.Stock is not null)
            .WithMessage("O estoque não pode ser negativo.");
        RuleFor(x => x.LowStockThreshold).GreaterThanOrEqualTo(0).When(x => x.LowStockThreshold is not null);
        RuleFor(x => x.IsActive).NotNull().When(_ => creating).WithMessage("Informe se o produto está ativo.");
        RuleFor(x => x.IsFeatured).NotNull().When(_ => creating).WithMessage("Informe se o produto é destaque.");
        RuleFor(x => x.Sku).MaximumLength(40).Matches("^[A-Za-z0-9._-]*$").When(x => x.Sku is not null).WithMessage("SKU inválido.");
        RuleFor(x => x.Barcode).Matches(@"^\d{8,14}$").When(x => !string.IsNullOrEmpty(x.Barcode))
            .WithMessage("Código de barras deve ter de 8 a 14 dígitos.");
        RuleFor(x => x.CategoryIds).Must(ids => ids!.Count <= Product.MaxCategories).When(x => x.CategoryIds is not null)
            .WithMessage("Um produto pode ter no máximo 8 categorias.");
        RuleForEach(x => x.Variants).ChildRules(v =>
        {
            v.RuleFor(x => x.Label).NotEmpty().MaximumLength(40).WithMessage("Informe o nome da variação (até 40 caracteres).");
            v.RuleFor(x => x.PriceCents).GreaterThanOrEqualTo(1).WithMessage("O preço da variação deve ser de ao menos 1 centavo.");
            v.RuleFor(x => x.Sku).MaximumLength(40).Matches("^[A-Za-z0-9._-]*$").When(x => x.Sku is not null).WithMessage("SKU da variação inválido.");
        });
    }
}

public sealed record CreateProductCommand(ProductInput Input) : ICommand<ProductEnvelope>;

public sealed record UpdateProductCommand(Guid ProductId, ProductInput Input) : ICommand<ProductEnvelope>;

public sealed record DeleteProductCommand(Guid ProductId) : ICommand;

internal sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator() => RuleFor(x => x.Input).SetValidator(new ProductInputValidator(creating: true));
}

internal sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator() => RuleFor(x => x.Input).SetValidator(new ProductInputValidator(creating: false));
}

/// <summary>
/// Criar e atualizar compartilham o mesmo fluxo; só muda a origem dos valores
/// (formulário inteiro vs. formulário mesclado com o produto atual).
/// </summary>
internal sealed class ProductCommandHandlers(
    ITenantContext tenantContext,
    ICatalogRepository catalog,
    ICatalogQueries queries,
    ICatalogCache cache,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<CreateProductCommand, ProductEnvelope>,
      ICommandHandler<UpdateProductCommand, ProductEnvelope>,
      ICommandHandler<DeleteProductCommand>
{
    public async Task<Result<ProductEnvelope>> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        var tenant = tenantContext.Tenant!;
        var limits = PlanCatalog.For(tenant.EffectivePlan);
        if (limits.MaxProducts is { } max && await catalog.CountProductsAsync(cancellationToken) >= max)
        {
            return CatalogErrors.ProductLimit(max);
        }

        var draft = await BuildDraftAsync(request.Input, current: null, cancellationToken);
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        var created = Product.Create(tenant.Id, draft.Value, clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error;
        }

        catalog.Add(created.Value);
        return await SaveAndReadAsync(created.Value, cancellationToken);
    }

    public async Task<Result<ProductEnvelope>> Handle(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await catalog.GetProductAsync(new ProductId(request.ProductId), cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var draft = await BuildDraftAsync(request.Input, product, cancellationToken);
        if (draft.IsFailure)
        {
            return draft.Error;
        }

        var updated = product.Update(draft.Value, clock.GetUtcNow());
        return updated.IsFailure ? updated.Error : await SaveAndReadAsync(product, cancellationToken);
    }

    public async Task<Result> Handle(DeleteProductCommand request, CancellationToken cancellationToken)
    {
        var product = await catalog.GetProductAsync(new ProductId(request.ProductId), cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        catalog.Remove(product); // vínculos com coleções saem junto (FK em cascata)
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await cache.InvalidateAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result<ProductDraft>> BuildDraftAsync(ProductInput input, Product? current, CancellationToken cancellationToken)
    {
        var limits = PlanCatalog.For(tenantContext.Tenant!.EffectivePlan);
        if (input.Images is { } images && images.Count > limits.MaxImagesPerProduct)
        {
            return CatalogErrors.ImageLimit(limits.MaxImagesPerProduct);
        }

        var category = input.Category is not null
            ? Enum.Parse<ProductCategory>(input.Category, ignoreCase: true)
            : current!.Category;

        // Categorias dinâmicas: as informadas (todas ativas e da loja) ou, ao criar,
        // a categoria dinâmica com o mesmo slug da categoria legada.
        IReadOnlyList<CategoryId> categoryIds;
        if (input.CategoryIds is { Count: > 0 } requested)
        {
            if (requested.Distinct().Count() != requested.Count)
            {
                return CatalogErrors.DuplicateCategories;
            }

            var ids = requested.Select(id => new CategoryId(id)).ToList();
            if (await catalog.CountActiveCategoriesAsync(ids, cancellationToken) != ids.Count)
            {
                return CatalogErrors.InvalidCategories;
            }

            categoryIds = ids;
        }
        else if (current is not null && input.CategoryIds is null && input.Category is null)
        {
            categoryIds = [.. current.Categories.Select(c => c.CategoryId)];
        }
        else
        {
            var legacy = await catalog.FindActiveCategoryBySlugAsync(category.ToString().ToLowerInvariant(), cancellationToken);
            categoryIds = legacy is { } id ? [id] : [];
        }

        var variants = input.Variants
            ?? current?.Variants.Select(v => new VariantDraft(v.Id.Value, v.Sku, v.Label, v.PriceCents, v.IsActive)).ToList()
            ?? [];
        var skus = variants.Select(v => v.Sku?.Trim().ToUpperInvariant()).OfType<string>().Where(s => s.Length > 0).ToList();
        if (skus.Count > 0 && await catalog.AnyVariantSkuTakenAsync(skus, current?.Id, cancellationToken))
        {
            return CatalogErrors.VariantSkuTaken;
        }

        // Slug: o informado; senão, se o nome mudou, gera do nome; senão, mantém.
        var slug = !string.IsNullOrWhiteSpace(input.Slug) ? input.Slug
            : input.Name is not null ? null
            : current?.Slug.Value;

        return new ProductDraft(
            input.Name ?? current!.Name,
            slug,
            input.ShortDescription ?? current!.ShortDescription,
            input.Description ?? current!.Description,
            category,
            categoryIds,
            input.Price ?? current!.PriceCents,
            input.DiscountPrice.Or(current?.DiscountPriceCents),
            input.Cost.Or(current?.CostCents),
            input.Images ?? current!.Images,
            input.Stock ?? current!.Stock,
            input.LowStockThreshold ?? current?.LowStockThreshold ?? 3,
            input.IsActive ?? current!.IsActive,
            input.IsFeatured ?? current!.IsFeatured,
            input.Sku ?? current?.Sku,
            input.Barcode ?? current?.Barcode,
            variants);
    }

    private async Task<Result<ProductEnvelope>> SaveAndReadAsync(Product product, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException ex) when (ex.ConstraintName.Contains("slug", StringComparison.Ordinal))
        {
            return CatalogErrors.ProductSlugTaken(product.Slug.Value);
        }
        catch (UniqueConstraintViolationException)
        {
            return Error.Conflict("CONFLICT", "Já existe um produto com este SKU ou código de barras.");
        }

        await cache.InvalidateAsync(cancellationToken);
        var saved = await queries.GetProductBySlugAsync(product.Slug.Value, includeCost: true, cancellationToken);
        return new ProductEnvelope(saved!);
    }
}
