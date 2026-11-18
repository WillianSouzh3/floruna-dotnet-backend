using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Floruna.SharedKernel;

namespace Floruna.Domain.Catalog;

/// <summary>Endereço legível de produto/categoria/coleção (<c>buque-de-rosas</c>). Único por loja.</summary>
public sealed partial record CatalogSlug
{
    public const int MaxLength = 60;

    private CatalogSlug(string value) => Value = value;

    public string Value { get; }

    /// <summary>Usa o slug informado ou gera a partir do nome (igual ao <c>slugify</c> do Nest).</summary>
    public static Result<CatalogSlug> FromInputOrName(string? slug, string name)
    {
        var value = string.IsNullOrWhiteSpace(slug) ? Slugify(name) : slug.Trim();
        if (value.Length is 0 or > MaxLength || !Pattern().IsMatch(value))
        {
            return Error.Validation("VALIDATION_ERROR", "Slug inválido.", ["Slug inválido."]);
        }

        return new CatalogSlug(value);
    }

    public static CatalogSlug FromPersistence(string value) => new(value);

    /// <summary>"Buquê de Rosas!" → "buque-de-rosas": remove acentos, minúsculas, espaços viram hífen.</summary>
    public static string Slugify(string text)
    {
        var decomposed = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        var lower = builder.ToString().ToLowerInvariant().Trim();
        var cleaned = NotAllowed().Replace(lower, string.Empty);
        cleaned = Whitespace().Replace(cleaned, "-");
        cleaned = RepeatedHyphens().Replace(cleaned, "-");
        return cleaned.Length > MaxLength ? cleaned[..MaxLength].TrimEnd('-') : cleaned;
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"[^a-z0-9\s-]")]
    private static partial Regex NotAllowed();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("-+")]
    private static partial Regex RepeatedHyphens();
}
