using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Floruna.SharedKernel;

namespace Floruna.Application.Identity;

/// <summary>
/// Confere, a cada requisição autenticada da equipe, se o token ainda vale:
/// o usuário existe, tem o mesmo papel e a mesma <c>sessionVersion</c>.
/// </summary>
/// <remarks>
/// Diferença consciente em relação ao Nest: lá o <c>caixa</c> não passava
/// por esta checagem (só <c>ADMIN_ROLES</c>). Aqui todo papel de equipe
/// passa, então revogar a sessão do caixa também funciona.
/// </remarks>
public sealed record ValidateStaffSessionQuery(Guid UserId, string Role, int SessionVersion) : IQuery<bool>;

internal sealed class ValidateStaffSessionHandler(IStaffUserRepository users)
    : IQueryHandler<ValidateStaffSessionQuery, bool>
{
    public async Task<Result<bool>> Handle(ValidateStaffSessionQuery request, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(new StaffUserId(request.UserId), cancellationToken);
        if (user is null || user.Role != request.Role || user.SessionVersion != request.SessionVersion)
        {
            return IdentityErrors.SessionRevoked;
        }

        return true;
    }
}

/// <summary>"Sair de todos os dispositivos" (<c>POST /auth/admin/revoke-sessions</c>).</summary>
public sealed record RevokeStaffSessionsCommand(Guid UserId) : ICommand;

internal sealed class RevokeStaffSessionsHandler(IStaffUserRepository users, IUnitOfWork unitOfWork)
    : ICommandHandler<RevokeStaffSessionsCommand>
{
    public async Task<Result> Handle(RevokeStaffSessionsCommand request, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(new StaffUserId(request.UserId), cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        user.RevokeSessions();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
