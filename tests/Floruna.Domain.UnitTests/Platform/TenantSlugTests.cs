using Floruna.Domain.Platform;

namespace Floruna.Domain.UnitTests.Platform;

public sealed class TenantSlugTests
{
    [Theory]
    [InlineData("flora-lis")]
    [InlineData("abc")]
    [InlineData("loja123")]
    [InlineData("a234567890123456789012345678901234567890")] // 40: limite
    [InlineData("  Jardim-Secreto  ")] // normaliza: trim + minúsculas
    public void Valid_slugs_are_accepted_and_normalized(string raw)
    {
        var slug = TenantSlug.Create(raw);

        slug.IsSuccess.ShouldBeTrue();
        slug.Value.Value.ShouldBe(raw.Trim().ToLowerInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]                 // curto demais
    [InlineData("-flora")]             // começa com hífen
    [InlineData("flora-")]             // termina com hífen
    [InlineData("flora lis")]          // espaço
    [InlineData("flôra")]              // acento
    [InlineData("a2345678901234567890123456789012345678901")] // 41 caracteres
    public void Invalid_slugs_are_rejected_as_validation_errors(string raw)
    {
        TenantSlug.Create(raw).Error.Code.ShouldBe("VALIDATION_ERROR");
    }

    [Theory]
    [InlineData("www")]
    [InlineData("api")]
    [InlineData("admin")]
    [InlineData("suporte")]
    public void Reserved_slugs_are_rejected_as_conflict(string raw)
    {
        TenantSlug.Create(raw).Error.ShouldBe(TenantErrors.SlugReserved);
    }
}
