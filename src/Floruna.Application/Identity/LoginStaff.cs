using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Identity;

/// <summary>Login da equipe no painel (<c>POST /auth/admin/login</c>).</summary>
public sealed record LoginStaffCommand(string Email, string Password) : ICommand<AuthResult>;

internal sealed class LoginStaffValidator : AbstractValidator<LoginStaffCommand>
{
    public LoginStaffValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("E-mail inválido.");
        RuleFor(x => x.Password).MinimumLength(6).WithMessage("A senha deve ter ao menos 6 caracteres.");
    }
}

internal sealed class LoginStaffHandler(
    ITenantContext tenantContext,
    IStaffUserRepository users,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer tokens) : ICommandHandler<LoginStaffCommand, AuthResult>
{
    public async Task<Result<AuthResult>> Handle(LoginStaffCommand request, CancellationToken cancellationToken)
    {
        if (tenantContext.Tenant is null)
        {
            return TenantErrors.NotResolved;
        }

        var email = Email.Create(request.Email);
        var user = email.IsSuccess ? await users.FindByEmailAsync(email.Value, cancellationToken) : null;

        // Sempre roda o Verify, mesmo sem usuário: tempo constante (anti-enumeração).
        if (!passwordHasher.Verify(request.Password, user?.PasswordHash) || user is null)
        {
            return IdentityErrors.InvalidCredentials;
        }

        return new AuthResult(
            tokens.IssueFor(user),
            new AuthUser(user.Id.ToString(), user.Name, user.Email.Value, user.Role));
    }
}
