using Floruna.SharedKernel;

namespace Floruna.Domain.Catalog;

public static class CatalogErrors
{
    public static readonly Error ProductNotFound = Error.NotFound("NOT_FOUND", "Produto não encontrado.");
    public static readonly Error CategoryNotFound = Error.NotFound("NOT_FOUND", "Categoria não encontrada.");
    public static readonly Error CollectionNotFound = Error.NotFound("NOT_FOUND", "Coleção não encontrada.");

    public static readonly Error DiscountNotBelowPrice = Validation("O preço promocional deve ser menor que o preço.");
    public static readonly Error ImagesRequired = Validation("Informe ao menos uma imagem.");
    public static readonly Error TooManyVariants = Validation("Um produto pode ter no máximo 12 variações.");
    public static readonly Error DuplicateVariantLabel = Validation("As variações não podem ter nomes repetidos.");
    public static readonly Error DuplicateVariantId = Validation("As variações não podem ter IDs repetidos.");
    public static readonly Error DuplicateVariantSku = Validation("As variações não podem ter SKUs repetidos.");
    public static readonly Error VariantSkuTaken = Validation("Já existe uma variação com um dos SKUs informados.");
    public static readonly Error DuplicateCategories = Validation("Categorias não podem ser repetidas.");
    public static readonly Error InvalidCategories = Validation("Uma das categorias não existe, pertence a outra loja ou está inativa.");
    public static readonly Error CollectionWindowInvalid = Validation("A data de término deve ser após o início.");
    public static readonly Error DuplicateCollectionProducts = Validation("Produtos não podem ser repetidos na coleção.");
    public static readonly Error UnknownCollectionProducts = Validation("A coleção referencia produto inexistente ou de outra loja.");
    public static readonly Error CategoryInUse = Error.Conflict("CONFLICT", "A categoria possui produtos vinculados e não pode ser removida.");

    public static Error VariantNotInProduct(string label) => Validation($"A variação \"{label}\" não pertence a este produto.");

    public static Error ProductSlugTaken(string slug) => Validation($"Já existe um produto com o slug \"{slug}\".");

    public static Error SlugTaken(string entity) => Error.Conflict("CONFLICT", $"Já existe uma {entity} com este endereço.");

    public static Error ProductLimit(int max) => Error.Forbidden(
        "PLAN_UPGRADE_REQUIRED", $"Seu plano permite até {max} produtos. Faça upgrade para o plano Profissional para cadastrar mais.");

    public static Error ImageLimit(int max) => Error.Forbidden(
        "PLAN_UPGRADE_REQUIRED", $"Seu plano permite até {max} fotos por produto. Faça upgrade para ampliar.");

    private static Error Validation(string message) => Error.Validation("VALIDATION_ERROR", message, [message]);
}
