using System.Text.RegularExpressions;
using Floruna.SharedKernel;

namespace Floruna.Domain.Platform;

/// <summary>
/// Endereço da loja na plataforma (<c>{slug}.floruna.com.br</c>). Imutável
/// depois de criado: mudar o slug quebraria links, e-mails e o domínio.
/// </summary>
public sealed partial record TenantSlug
{
    /// <summary>Endereços que a plataforma usa para si (mesma lista do Nest).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "www", "api", "app", "admin", "painel", "plataforma", "docs", "status", "mail", "suporte",
    };

    private TenantSlug(string value) => Value = value;

    public string Value { get; }

    public static Result<TenantSlug> Create(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant() ?? string.Empty;

        if (!Pattern().IsMatch(value))
        {
            return Error.Validation(
                "VALIDATION_ERROR",
                "Endereço inválido: use 3–40 letras minúsculas, números e hífens.",
                ["Endereço inválido: use 3–40 letras minúsculas, números e hífens."]);
        }

        if (Reserved.Contains(value))
        {
            return TenantErrors.SlugReserved;
        }

        return new TenantSlug(value);
    }

    /// <summary>Reconstrói a partir do banco, sem revalidar (o valor já foi validado ao criar).</summary>
    public static TenantSlug FromPersistence(string value) => new(value);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38}[a-z0-9])$")]
    private static partial Regex Pattern();
}
