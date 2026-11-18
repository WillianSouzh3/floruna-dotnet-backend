using System.Globalization;
using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Catalog;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Catalog;

public sealed record CategoryInput(string? Name, string? Slug, string? Description, bool? IsActive);

public sealed record CreateCategoryCommand(CategoryInput Input) : ICommand<CategoryEnvelope>;

public sealed record UpdateCategoryCommand(Guid CategoryId, CategoryInput Input) : ICommand<CategoryEnvelope>;

public sealed record DeleteCategoryCommand(Guid CategoryId) : ICommand;

internal sealed class CreateCategoryValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryValidator()
    {
        RuleFor(x => x.Input.Name).NotEmpty().Length(2, 120).WithMessage("O nome deve ter entre 2 e 120 caracteres.");
        RuleFor(x => x.Input.Description).MaximumLength(500).WithMessage("A descrição deve ter até 500 caracteres.");
    }
}

internal sealed class UpdateCategoryValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryValidator()
    {
        RuleFor(x => x.Input.Name).Length(2, 120).When(x => x.Input.Name is not null).WithMessage("O nome deve ter entre 2 e 120 caracteres.");
        RuleFor(x => x.Input.Description).MaximumLength(500).WithMessage("A descrição deve ter até 500 caracteres.");
    }
}

internal sealed class CategoryCommandHandlers(
    ITenantContext tenantContext,
    ICatalogRepository catalog,
    ICatalogCache cache,
    IUnitOfWork unitOfWork)
    : ICommandHandler<CreateCategoryCommand, CategoryEnvelope>,
      ICommandHandler<UpdateCategoryCommand, CategoryEnvelope>,
      ICommandHandler<DeleteCategoryCommand>
{
    public async Task<Result<CategoryEnvelope>> Handle(CreateCategoryCommand request, CancellationToken cancellationToken)
    {
        var input = request.Input;
        var created = Category.Create(tenantContext.RequiredTenantId, input.Name!, input.Slug, input.Description, input.IsActive ?? true);
        if (created.IsFailure)
        {
            return created.Error;
        }

        catalog.Add(created.Value);
        return await SaveAsync(created.Value, cancellationToken);
    }

    public async Task<Result<CategoryEnvelope>> Handle(UpdateCategoryCommand request, CancellationToken cancellationToken)
    {
        var category = await catalog.GetCategoryAsync(new CategoryId(request.CategoryId), cancellationToken);
        if (category is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        var input = request.Input;
        var slug = input.Slug ?? (input.Name is null ? category.Slug.Value : null);
        var updated = category.Update(input.Name ?? category.Name, slug, input.Description ?? category.Description, input.IsActive ?? category.IsActive);
        return updated.IsFailure ? updated.Error : await SaveAsync(category, cancellationToken);
    }

    public async Task<Result> Handle(DeleteCategoryCommand request, CancellationToken cancellationToken)
    {
        var category = await catalog.GetCategoryAsync(new CategoryId(request.CategoryId), cancellationToken);
        if (category is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        if (await catalog.CategoryHasProductsAsync(category.Id, cancellationToken))
        {
            return CatalogErrors.CategoryInUse;
        }

        catalog.Remove(category);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await cache.InvalidateAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result<CategoryEnvelope>> SaveAsync(Category category, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return CatalogErrors.SlugTaken("categoria");
        }

        await cache.InvalidateAsync(cancellationToken);
        return new CategoryEnvelope(CatalogMapping.ToResponse(category));
    }
}

public sealed record CollectionInput(string? Name, string? Slug, string? Description, bool? IsActive, Optional<DateTimeOffset?> StartsAt, Optional<DateTimeOffset?> EndsAt);

public sealed record CreateCollectionCommand(CollectionInput Input) : ICommand<CollectionEnvelope>;

public sealed record UpdateCollectionCommand(Guid CollectionId, CollectionInput Input) : ICommand<CollectionEnvelope>;

public sealed record ReplaceCollectionProductsCommand(Guid CollectionId, IReadOnlyList<Guid> ProductIds) : ICommand<CollectionWithProductIds>;

public sealed record DeleteCollectionCommand(Guid CollectionId) : ICommand;

internal sealed class CreateCollectionValidator : AbstractValidator<CreateCollectionCommand>
{
    public CreateCollectionValidator()
    {
        RuleFor(x => x.Input.Name).NotEmpty().Length(2, 120).WithMessage("O nome deve ter entre 2 e 120 caracteres.");
        RuleFor(x => x.Input.Description).MaximumLength(500).WithMessage("A descrição deve ter até 500 caracteres.");
    }
}

internal sealed class CollectionCommandHandlers(
    ITenantContext tenantContext,
    ICatalogRepository catalog,
    ICatalogCache cache,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<CreateCollectionCommand, CollectionEnvelope>,
      ICommandHandler<UpdateCollectionCommand, CollectionEnvelope>,
      ICommandHandler<ReplaceCollectionProductsCommand, CollectionWithProductIds>,
      ICommandHandler<DeleteCollectionCommand>
{
    public async Task<Result<CollectionEnvelope>> Handle(CreateCollectionCommand request, CancellationToken cancellationToken)
    {
        var i = request.Input;
        var created = Collection.Create(tenantContext.RequiredTenantId, i.Name!, i.Slug, i.Description, i.IsActive ?? false, i.StartsAt.Value, i.EndsAt.Value);
        if (created.IsFailure)
        {
            return created.Error;
        }

        catalog.Add(created.Value);
        return await SaveAsync(created.Value, cancellationToken);
    }

    public async Task<Result<CollectionEnvelope>> Handle(UpdateCollectionCommand request, CancellationToken cancellationToken)
    {
        var collection = await catalog.GetCollectionAsync(new CollectionId(request.CollectionId), cancellationToken);
        if (collection is null)
        {
            return CatalogErrors.CollectionNotFound;
        }

        var i = request.Input;
        var updated = collection.Update(
            i.Name ?? collection.Name,
            i.Slug ?? (i.Name is null ? collection.Slug.Value : null),
            i.Description ?? collection.Description,
            i.IsActive ?? collection.IsActive,
            i.StartsAt.Or(collection.StartsAt),
            i.EndsAt.Or(collection.EndsAt));
        return updated.IsFailure ? updated.Error : await SaveAsync(collection, cancellationToken);
    }

    public async Task<Result<CollectionWithProductIds>> Handle(ReplaceCollectionProductsCommand request, CancellationToken cancellationToken)
    {
        var collection = await catalog.GetCollectionAsync(new CollectionId(request.CollectionId), cancellationToken);
        if (collection is null)
        {
            return CatalogErrors.CollectionNotFound;
        }

        var ids = request.ProductIds.Select(id => new ProductId(id)).ToList();
        var replaced = collection.ReplaceProducts(ids);
        if (replaced.IsFailure)
        {
            return replaced.Error;
        }

        if (await catalog.CountExistingProductsAsync(ids.Distinct().ToList(), cancellationToken) != ids.Count)
        {
            return CatalogErrors.UnknownCollectionProducts;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await cache.InvalidateAsync(cancellationToken);
        return new CollectionWithProductIds(CatalogMapping.ToResponse(collection, clock.GetUtcNow()), [.. ids.Select(id => id.ToString())]);
    }

    public async Task<Result> Handle(DeleteCollectionCommand request, CancellationToken cancellationToken)
    {
        var collection = await catalog.GetCollectionAsync(new CollectionId(request.CollectionId), cancellationToken);
        if (collection is null)
        {
            return CatalogErrors.CollectionNotFound;
        }

        catalog.Remove(collection);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await cache.InvalidateAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result<CollectionEnvelope>> SaveAsync(Collection collection, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return CatalogErrors.SlugTaken("coleção");
        }

        await cache.InvalidateAsync(cancellationToken);
        return new CollectionEnvelope(CatalogMapping.ToResponse(collection, clock.GetUtcNow()));
    }
}

/// <summary>Agregado → DTO de resposta (usado após escrita; as leituras projetam direto no SQL).</summary>
public static class CatalogMapping
{
    public static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static CategoryResponse ToResponse(Category c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(c.Id.ToString(), c.Name, c.Slug.Value, c.Description, c.IsActive);
    }

    public static CollectionResponse ToResponse(Collection c, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(
            c.Id.ToString(), c.Name, c.Slug.Value, c.Description, c.IsActive,
            c.StartsAt is { } s ? Iso(s) : null, c.EndsAt is { } e ? Iso(e) : null,
            c.StateAt(now).ToString().ToLowerInvariant(), c.IsVisibleAt(now));
    }
}
