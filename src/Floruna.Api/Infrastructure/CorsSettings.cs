using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Floruna.Api.Infrastructure;

/// <summary>
/// OPTIONS PATTERN: a seção <c>Cors</c> do appsettings/variáveis de ambiente
/// vira um objeto fortemente tipado, validado NA SUBIDA (fail-fast).
/// Equivale ao Joi que valida o ambiente no boot do Nest.
/// </summary>
/// <remarks>
/// Em variável de ambiente (ECS/docker): <c>Cors__AllowedOrigins__0=https://floruna.com.br</c>.
/// </remarks>
public sealed class CorsSettings
{
    public const string SectionName = "Cors";

    [Required]
    [MinLength(1, ErrorMessage = "Informe ao menos uma origem em Cors:AllowedOrigins.")]
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}

/// <summary>
/// Validação que DataAnnotations não expressa: cada origem precisa ser uma
/// URL absoluta http(s) sem caminho. Mostra o segundo jeito de validar Options.
/// </summary>
internal sealed class CorsSettingsValidator : IValidateOptions<CorsSettings>
{
    public ValidateOptionsResult Validate(string? name, CorsSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var invalid = options.AllowedOrigins
            .Where(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.AbsolutePath != "/")
            .ToList();

        return invalid.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Origens CORS inválidas: {string.Join(", ", invalid)}");
    }
}
