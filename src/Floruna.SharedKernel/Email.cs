using System.Net.Mail;

namespace Floruna.SharedKernel;

/// <summary>E-mail normalizado (minúsculas, sem espaços). Value object.</summary>
public sealed record Email
{
    public const int MaxLength = 254;

    private Email(string value) => Value = value;

    public string Value { get; }

    public static Result<Email> Create(string? raw)
    {
        var value = raw?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Length is 0 or > MaxLength || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value)
        {
            return Error.Validation("VALIDATION_ERROR", "E-mail inválido.", ["E-mail inválido."]);
        }

        return new Email(value);
    }

    /// <summary>Reconstrói a partir do banco, sem revalidar.</summary>
    public static Email FromPersistence(string value) => new(value);

    public override string ToString() => Value;
}
