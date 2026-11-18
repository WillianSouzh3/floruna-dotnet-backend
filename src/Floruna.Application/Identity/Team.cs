using System.Globalization;
using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Audit;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Identity;

/// <summary>Membro da equipe como o front conhece (sem hash de senha, tenant ou sessionVersion).</summary>
public sealed record TeamMemberResponse(string Id, string Name, string Email, string Role, string? Locale, string CreatedAt)
{
    public static TeamMemberResponse From(StaffUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new(user.Id.ToString(), user.Name, user.Email.Value, user.Role, user.Locale,
            user.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }
}

public sealed record TeamResponse(IReadOnlyList<TeamMemberResponse> Users);

/// <summary>Equipe do painel (<c>GET /users</c>). Query: só leitura, sem transação.</summary>
public sealed record ListTeamQuery : IQuery<TeamResponse>;

internal sealed class ListTeamHandler(IStaffUserRepository users) : IQueryHandler<ListTeamQuery, TeamResponse>
{
    public async Task<Result<TeamResponse>> Handle(ListTeamQuery request, CancellationToken cancellationToken)
    {
        var members = await users.ListAsync(cancellationToken);
        return new TeamResponse([.. members.Where(u => u.Role != Roles.SuperAdmin).Select(TeamMemberResponse.From)]);
    }
}

/// <summary>Adiciona um membro (<c>POST /users</c>), respeitando o teto de usuários do plano (RF-IAM-06).</summary>
public sealed record CreateTeamMemberCommand(string Name, string Email, string Password, string Role, AuditActor Actor)
    : ICommand<TeamMemberResponse>;

internal sealed class CreateTeamMemberValidator : AbstractValidator<CreateTeamMemberCommand>
{
    public CreateTeamMemberValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(80).WithMessage("O nome deve ter entre 2 e 80 caracteres.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("Email inválido.");
        RuleFor(x => x.Password).MinimumLength(12).WithMessage("A senha deve ter no mínimo 12 caracteres.");
        RuleFor(x => x.Password).MaximumLength(72).WithMessage("A senha deve ter no máximo 72 caracteres.");
        RuleFor(x => x.Role).Must(TeamPolicy.AssignableRoles.Contains).WithMessage("Papel inválido.");
    }
}

internal sealed class CreateTeamMemberHandler(
    ITenantContext tenantContext,
    IStaffUserRepository users,
    IPasswordHasher passwordHasher,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<CreateTeamMemberCommand, TeamMemberResponse>
{
    public static readonly Error EmailTaken = Error.Conflict("CONFLICT", "Já existe um usuário com este email na loja.");

    public async Task<Result<TeamMemberResponse>> Handle(CreateTeamMemberCommand request, CancellationToken cancellationToken)
    {
        if (tenantContext.Tenant is not { } tenant)
        {
            return TenantErrors.NotResolved;
        }

        var maxUsers = PlanCatalog.For(tenant.EffectivePlan).MaxUsers;
        if (await users.CountAsync(cancellationToken) >= maxUsers)
        {
            return TeamPolicy.PlanLimit(maxUsers);
        }

        var email = Email.Create(request.Email);
        if (email.IsFailure)
        {
            return email.Error;
        }

        var now = clock.GetUtcNow();
        var created = StaffUser.Create(tenant.Id, request.Name, email.Value, passwordHasher.Hash(request.Password), request.Role, now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        users.Add(created.Value);
        audit.Add(AuditEntry.Record(tenant.Id, AuditActions.TeamChanged, request.Actor, "user", created.Value.Id.ToString(),
            $"Usuário adicionado à equipe: {email.Value} ({request.Role})", now));
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return EmailTaken;
        }

        return TeamMemberResponse.From(created.Value);
    }
}

/// <summary>Muda papel e/ou senha de um membro (<c>PATCH /users/:id</c>).</summary>
public sealed record UpdateTeamMemberCommand(Guid UserId, string? Role, string? Password, AuditActor Actor) : ICommand<TeamMemberResponse>;

internal sealed class UpdateTeamMemberValidator : AbstractValidator<UpdateTeamMemberCommand>
{
    public UpdateTeamMemberValidator()
    {
        RuleFor(x => x).Must(x => x.Role is not null || x.Password is not null).WithMessage("Informe o papel e/ou a nova senha.");
        RuleFor(x => x.Role!).Must(TeamPolicy.AssignableRoles.Contains).When(x => x.Role is not null).WithMessage("Papel inválido.");
        RuleFor(x => x.Password!).Length(12, 72).When(x => x.Password is not null).WithMessage("A senha deve ter no mínimo 12 caracteres.");
    }
}

internal sealed class UpdateTeamMemberHandler(
    ITenantContext tenantContext,
    IStaffUserRepository users,
    IPasswordHasher passwordHasher,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<UpdateTeamMemberCommand, TeamMemberResponse>
{
    public async Task<Result<TeamMemberResponse>> Handle(UpdateTeamMemberCommand request, CancellationToken cancellationToken)
    {
        var target = await users.GetByIdAsync(new StaffUserId(request.UserId), cancellationToken);
        if (target is null || target.Role == Roles.SuperAdmin)
        {
            return TeamPolicy.MemberNotFound;
        }

        if (request.Role is not null)
        {
            var masters = await users.CountByRoleAsync(Roles.Master, cancellationToken);
            var allowed = TeamPolicy.CanChangeRole(target, request.Role, new StaffUserId(request.Actor.UserId ?? Guid.Empty), masters);
            if (allowed.IsFailure)
            {
                return allowed.Error;
            }

            var changed = target.ChangeRole(request.Role);
            if (changed.IsFailure)
            {
                return changed.Error;
            }
        }

        if (request.Password is not null)
        {
            target.ChangePasswordHash(passwordHasher.Hash(request.Password));
        }

        audit.Add(AuditEntry.Record(tenantContext.RequiredTenantId, AuditActions.TeamChanged, request.Actor, "user", target.Id.ToString(),
            $"Membro da equipe alterado: {target.Email.Value}", clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return TeamMemberResponse.From(target);
    }
}

/// <summary>Remove um membro (<c>DELETE /users/:id</c>).</summary>
public sealed record RemoveTeamMemberCommand(Guid UserId, AuditActor Actor) : ICommand;

internal sealed class RemoveTeamMemberHandler(
    ITenantContext tenantContext,
    IStaffUserRepository users,
    IAuditRepository audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<RemoveTeamMemberCommand>
{
    public async Task<Result> Handle(RemoveTeamMemberCommand request, CancellationToken cancellationToken)
    {
        var target = await users.GetByIdAsync(new StaffUserId(request.UserId), cancellationToken);
        if (target is null || target.Role == Roles.SuperAdmin)
        {
            return TeamPolicy.MemberNotFound;
        }

        var masters = await users.CountByRoleAsync(Roles.Master, cancellationToken);
        var allowed = TeamPolicy.CanRemove(target, new StaffUserId(request.Actor.UserId ?? Guid.Empty), masters);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        users.Remove(target);
        audit.Add(AuditEntry.Record(tenantContext.RequiredTenantId, AuditActions.TeamChanged, request.Actor, "user", target.Id.ToString(),
            $"Membro removido da equipe: {target.Email.Value}", clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
