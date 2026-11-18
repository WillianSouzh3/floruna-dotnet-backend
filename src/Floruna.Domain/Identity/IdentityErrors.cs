using Floruna.SharedKernel;

namespace Floruna.Domain.Identity;

public static class IdentityErrors
{
    /// <summary>
    /// Mesma mensagem para e-mail inexistente e senha errada: não revela
    /// quais e-mails têm conta (anti-enumeração).
    /// </summary>
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("AUTHENTICATION_REQUIRED", "E-mail ou senha incorretos.");

    public static readonly Error SessionRevoked =
        Error.Unauthorized("AUTHENTICATION_REQUIRED", "Sessão revogada. Entre novamente.");

    public static readonly Error SessionFromOtherTenant =
        Error.Unauthorized("AUTHENTICATION_REQUIRED", "Sessão não pertence a esta loja.");

    public static readonly Error UserNotFound =
        Error.Unauthorized("AUTHENTICATION_REQUIRED", "Usuário não encontrado.");

    public static readonly Error EmailTaken = Error.Conflict("CONFLICT", "Já existe uma conta com este e-mail.");

    public static readonly Error NameInvalid = Error.Validation(
        "VALIDATION_ERROR", "Nome inválido.", ["O nome deve ter entre 2 e 80 caracteres."]);

    public static readonly Error RoleInvalid = Error.Validation(
        "VALIDATION_ERROR", "Papel inválido.", ["Papel inválido."]);

    public static readonly Error PlanUserLimitReached = Error.Forbidden(
        "PLAN_UPGRADE_REQUIRED", "Seu plano atingiu o limite de usuários.");
}
