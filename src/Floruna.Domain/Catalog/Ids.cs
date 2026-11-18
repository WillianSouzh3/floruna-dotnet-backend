using System.Security.Cryptography;

namespace Floruna.Domain.Catalog;

public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct CategoryId(Guid Value)
{
    public static CategoryId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public readonly record struct CollectionId(Guid Value)
{
    public static CollectionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identidade ESTÁVEL da variação (<c>var_</c> + 24 hex, mesmo formato do Nest).
/// Pedidos, carrinhos e estoque apontam para este ID: renomear "M" para "Médio"
/// não pode quebrar nada (D-003).
/// </summary>
public readonly record struct VariantId(string Value)
{
    public static VariantId New() => new($"var_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))}");

    public static bool IsValid(string? value) =>
        value is { Length: 28 } && value.StartsWith("var_", StringComparison.Ordinal) && value[4..].All(Uri.IsHexDigit) && value == value.ToLowerInvariant();

    public override string ToString() => Value;
}
