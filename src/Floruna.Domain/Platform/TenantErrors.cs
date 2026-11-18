using Floruna.SharedKernel;

namespace Floruna.Domain.Platform;

/// <summary>
/// Catálogo de erros do contexto. Um lugar só para código + mensagem:
/// o handler devolve <c>TenantErrors.SlugTaken</c>, sem string solta.
/// </summary>
public static class TenantErrors
{
    public static readonly Error SlugReserved = Error.Conflict("CONFLICT", "Este endereço de loja é reservado.");

    public static readonly Error SlugTaken = Error.Conflict("CONFLICT", "Este endereço de loja já está em uso.");

    public static readonly Error NameInvalid = Error.Validation(
        "VALIDATION_ERROR", "Nome da loja inválido.", ["O nome da loja deve ter entre 2 e 80 caracteres."]);

    public static readonly Error Suspended = Error.Forbidden("FORBIDDEN", "Esta loja está temporariamente suspensa.");

    public static readonly Error NotResolved = Error.Unauthorized("AUTHENTICATION_REQUIRED", "Loja não resolvida.");
}
