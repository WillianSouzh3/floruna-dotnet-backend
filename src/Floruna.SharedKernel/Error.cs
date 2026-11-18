namespace Floruna.SharedKernel;

/// <summary>
/// Categoria do erro. A camada HTTP traduz para status code; o domínio
/// nunca fala em HTTP.
/// </summary>
public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Unauthorized = 4,
    Forbidden = 5,
    TooManyRequests = 6,
}

/// <summary>
/// Erro de negócio esperado, tratado como VALOR (padrão Result), não como exceção.
/// </summary>
/// <param name="Code">Código estável do contrato da API (ex.: <c>COUPON_EXPIRED</c>). O front decide pelo código.</param>
/// <param name="Message">Diagnóstico legível em pt-BR. Pode mudar sem versionar a API.</param>
/// <param name="Type">Categoria, usada para escolher o status HTTP.</param>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Failure)
{
    /// <summary>Detalhes por campo, presentes em erros de validação.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>Em bloqueios temporários: quantos segundos esperar (vira o header <c>Retry-After</c>).</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>Representa "nenhum erro" dentro de um <see cref="Result"/> de sucesso.</summary>
    public static readonly Error None = new(string.Empty, string.Empty);

    public static Error Validation(string code, string message, IReadOnlyList<string>? issues = null) =>
        new(code, message, ErrorType.Validation) { Issues = issues ?? [] };

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error TooManyRequests(string code, string message, int retryAfterSeconds) =>
        new(code, message, ErrorType.TooManyRequests) { RetryAfterSeconds = retryAfterSeconds };
}
