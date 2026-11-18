using Floruna.Domain.Catalog;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Catalog;

public sealed class ProductTests
{
    private static readonly TenantId Store = TenantId.New();
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static ProductDraft Draft(
        long price = 19_900,
        long? discount = null,
        IReadOnlyList<VariantDraft>? variants = null,
        string name = "Buquê de Rosas Vermelhas",
        string? slug = null) => new(
        name, slug, "Doze rosas colombianas", "Buquê com doze rosas vermelhas e folhagens.",
        ProductCategory.Buques, [], price, discount, 8_000, ["https://cdn/rosa.webp"], 3, true, false, "buq-001", null,
        variants ?? []);

    [Fact]
    public void New_product_generates_slug_from_name_and_keeps_money_in_cents()
    {
        var product = Product.Create(Store, Draft(), Now).Value;

        product.Slug.Value.ShouldBe("buque-de-rosas-vermelhas");
        product.Price.ShouldBe(Money.FromCents(19_900));
        product.Sku.ShouldBe("BUQ-001");
        product.EffectivePrice.ShouldBe(product.Price);
    }

    [Theory]
    [InlineData(19_900)]
    [InlineData(25_000)]
    public void Discount_must_be_below_price(long discount)
    {
        Product.Create(Store, Draft(discount: discount), Now).Error.ShouldBe(CatalogErrors.DiscountNotBelowPrice);
    }

    [Fact]
    public void Effective_price_is_the_discount_when_present()
    {
        Product.Create(Store, Draft(discount: 15_000), Now).Value.EffectivePrice.ShouldBe(Money.FromCents(15_000));
    }

    [Fact]
    public void Variants_get_stable_ids_that_survive_a_rename()
    {
        var product = Product.Create(Store, Draft(variants: [new(null, "BUQ-P", "P", 15_900, null), new(null, null, "M", 19_900, null)]), Now).Value;
        var mediumId = product.Variants.Single(v => v.Label == "M").Id;
        mediumId.Value.ShouldMatch("^var_[0-9a-f]{24}$");

        product.Update(Draft(variants: [new(mediumId.Value, null, "Médio", 21_900, null)]), Now).IsSuccess.ShouldBeTrue();

        var renamed = product.Variants.ShouldHaveSingleItem();
        renamed.Id.ShouldBe(mediumId);
        renamed.Label.ShouldBe("Médio");
        renamed.Price.ShouldBe(Money.FromCents(21_900));
    }

    [Fact]
    public void Variant_without_id_matches_an_existing_one_by_label()
    {
        var product = Product.Create(Store, Draft(variants: [new(null, null, "M", 19_900, null)]), Now).Value;
        var id = product.Variants[0].Id;

        product.Update(Draft(variants: [new(null, null, " m ", 20_900, null)]), Now);

        product.Variants[0].Id.ShouldBe(id);
    }

    [Fact]
    public void Variant_id_from_another_product_is_rejected()
    {
        var product = Product.Create(Store, Draft(variants: [new(null, null, "M", 19_900, null)]), Now).Value;
        var foreign = VariantId.New().Value;

        product.Update(Draft(variants: [new(foreign, null, "G", 25_900, null)]), Now).Error.Code.ShouldBe("VALIDATION_ERROR");
    }

    [Fact]
    public void Variant_labels_and_skus_must_be_unique()
    {
        Product.Create(Store, Draft(variants: [new(null, null, "M", 1, null), new(null, null, "m", 2, null)]), Now)
            .Error.ShouldBe(CatalogErrors.DuplicateVariantLabel);
        Product.Create(Store, Draft(variants: [new(null, "X1", "P", 1, null), new(null, "x1", "G", 2, null)]), Now)
            .Error.ShouldBe(CatalogErrors.DuplicateVariantSku);
    }

    [Fact]
    public void At_most_twelve_variants()
    {
        var variants = Enumerable.Range(1, 13).Select(i => new VariantDraft(null, null, $"V{i}", 100, null)).ToList();

        Product.Create(Store, Draft(variants: variants), Now).Error.ShouldBe(CatalogErrors.TooManyVariants);
    }

    [Theory]
    [InlineData("Arranjo de Lírios & Orquídeas", "arranjo-de-lirios-orquideas")]
    [InlineData("  Cesta   Café da Manhã  ", "cesta-cafe-da-manha")]
    [InlineData("Girassóis!!!", "girassois")]
    public void Slugify_removes_accents_and_symbols(string name, string expected)
    {
        CatalogSlug.Slugify(name).ShouldBe(expected);
    }

    [Fact]
    public void Invalid_explicit_slug_is_rejected()
    {
        Product.Create(Store, Draft(slug: "Com Espaço"), Now).Error.Code.ShouldBe("VALIDATION_ERROR");
    }
}
