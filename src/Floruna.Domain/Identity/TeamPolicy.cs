using Floruna.SharedKernel;

namespace Floruna.Domain.Identity;

/// <summary>
/// Regras da equipe da loja que envolvem MAIS de um usuário (por isso não
/// cabem dentro do agregado <see cref="StaffUser"/>): domain service puro.
/// </summary>
public static class TeamPolicy
{
    /// <summary>Papéis que o dono pode atribuir pelo painel.</summary>
    public static readonly IReadOnlySet<string> AssignableRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        Roles.Master, Roles.Manager, Roles.Cashier,
    };

    public static readonly Error LastOwner = Error.Forbidden("FORBIDDEN", "A loja precisa de ao menos um proprietário (master).");
    public static readonly Error CannotDemoteSelf = Error.Forbidden("FORBIDDEN", "Você não pode rebaixar o próprio papel.");
    public static readonly Error CannotRemoveSelf = Error.Forbidden("FORBIDDEN", "Você não pode remover a própria conta.");
    public static readonly Error MemberNotFound = Error.NotFound("NOT_FOUND", "Usuário não encontrado.");

    public static Error PlanLimit(int maxUsers) => Error.Forbidden(
        "FORBIDDEN", $"Seu plano permite até {maxUsers} usuário(s). Faça upgrade para ampliar a equipe.");

    /// <summary>Trocar o papel: nunca deixar a loja sem master, nem o master rebaixar a si mesmo.</summary>
    public static Result CanChangeRole(StaffUser target, string newRole, StaffUserId actor, int masterCount)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Role == Roles.Master && newRole != Roles.Master)
        {
            if (target.Id == actor)
            {
                return CannotDemoteSelf;
            }

            if (masterCount <= 1)
            {
                return LastOwner;
            }
        }

        return Result.Success();
    }

    public static Result CanRemove(StaffUser target, StaffUserId actor, int masterCount)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Id == actor)
        {
            return CannotRemoveSelf;
        }

        return target.Role == Roles.Master && masterCount <= 1 ? LastOwner : Result.Success();
    }
}
