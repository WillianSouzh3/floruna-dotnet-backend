using Floruna.SharedKernel;

namespace Floruna.Application.Identity;

internal static class IdentityApplicationErrors
{
    public static Error Locked(TimeSpan remaining) => Error.TooManyRequests(
        "RATE_LIMITED",
        "Muitas tentativas de autenticação. Tente novamente mais tarde.",
        Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)));

    public static readonly Error PendingTokenInvalid = Error.Unauthorized("AUTHENTICATION_REQUIRED", "Token expirado ou inválido.");

    public static readonly Error CodeInvalid = Error.Unauthorized("AUTHENTICATION_REQUIRED", "Código incorreto ou expirado.");

    public static readonly Error ResetLinkInvalid = Error.Validation(
        "VALIDATION_ERROR", "Link inválido ou expirado. Solicite um novo.", ["Link inválido ou expirado. Solicite um novo."]);

    public static readonly Error CurrentPasswordWrong = Error.Unauthorized("AUTHENTICATION_REQUIRED", "Senha atual incorreta.");

    public static readonly Error ImpersonationGrantInvalid = Error.Unauthorized("AUTHENTICATION_REQUIRED", "Grant inválido, usado ou expirado.");
}
