using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Identity;

/// <summary>"Esqueci minha senha" da equipe (<c>POST /auth/admin/forgot-password</c>).</summary>
/// <param name="ResetBaseUrl">Origem da loja visitada (montada pelo controller a partir do host).</param>
public sealed record RequestPasswordResetCommand(string Email, string ResetBaseUrl) : ICommand;

internal sealed class RequestPasswordResetValidator : AbstractValidator<RequestPasswordResetCommand>
{
    public RequestPasswordResetValidator() =>
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("Email inválido.");
}

/// <summary>
/// SEMPRE responde sucesso, com ou sem conta: a resposta não pode revelar
/// quais e-mails estão cadastrados (anti-enumeração).
/// </summary>
internal sealed class RequestPasswordResetHandler(
    ITenantContext tenantContext,
    IStaffUserRepository users,
    ISingleUseTokenRepository singleUseTokens,
    ICredentialHasher credentialHasher,
    IEmailSender email,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<RequestPasswordResetCommand>
{
    public async Task<Result> Handle(RequestPasswordResetCommand request, CancellationToken cancellationToken)
    {
        var parsed = Email.Create(request.Email);
        if (tenantContext.Tenant is not { } tenant || parsed.IsFailure || !email.IsEnabled)
        {
            return Result.Success();
        }

        var user = await users.FindByEmailAsync(parsed.Value, cancellationToken);
        if (user is null)
        {
            return Result.Success();
        }

        var token = SecureRandom.HexToken();
        await singleUseTokens.RevokePendingAsync(user.Id, SingleUseTokenPurpose.PasswordReset, cancellationToken);
        singleUseTokens.Add(SingleUseToken.Issue(
            tenant.Id, SingleUseTokenPurpose.PasswordReset, user.Id, credentialHasher.HashOpaque(token), clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var link = $"{request.ResetBaseUrl.TrimEnd('/')}/admin/recuperar-senha?token={token}";
        await email.SendAsync(
            new EmailMessage(
                user.Email.Value,
                "Redefinição de senha",
                $"Olá, {user.Name}.\n\nPara criar uma nova senha, acesse:\n{link}\n\n" +
                "O link vale por 30 minutos e funciona uma única vez. Se não foi você que pediu, ignore este e-mail."),
            cancellationToken);

        return Result.Success();
    }
}

/// <summary>Conclui a redefinição com o token do e-mail (<c>POST /auth/admin/reset-password</c>).</summary>
public sealed record ResetPasswordCommand(string Token, string Password) : ICommand;

internal sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Token).Length(32, 128).WithMessage("Link inválido ou expirado. Solicite um novo.");
        RuleFor(x => x.Password).MinimumLength(12).WithMessage("A senha deve ter no mínimo 12 caracteres.");
        RuleFor(x => x.Password).MaximumLength(72).WithMessage("A senha deve ter no máximo 72 caracteres.");
    }
}

internal sealed class ResetPasswordHandler(
    ISingleUseTokenRepository singleUseTokens,
    ICredentialHasher credentialHasher,
    IStaffUserRepository users,
    IPasswordHasher passwordHasher,
    ILoginAttemptGuard attempts,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<ResetPasswordCommand>
{
    public async Task<Result> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        // Consumo atômico ANTES de trocar a senha. O filtro da loja faz um link da
        // loja A não funcionar no host da loja B.
        var consumed = await singleUseTokens.TryConsumeAsync(
            SingleUseTokenPurpose.PasswordReset, credentialHasher.HashOpaque(request.Token), clock.GetUtcNow(), cancellationToken);
        var user = consumed is null ? null : await users.GetByIdAsync(consumed.UserId, cancellationToken);
        if (user is null)
        {
            return IdentityApplicationErrors.ResetLinkInvalid;
        }

        user.ChangePasswordHash(passwordHasher.Hash(request.Password)); // também revoga as sessões abertas
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await attempts.ClearAsync(LoginScope.Staff, user.Email.Value, cancellationToken);
        return Result.Success();
    }
}

/// <summary>Troca da própria senha, exigindo a atual (<c>PATCH /users/me/password</c>).</summary>
public sealed record ChangeMyPasswordCommand(Guid UserId, string CurrentPassword, string NewPassword) : ICommand;

internal sealed class ChangeMyPasswordValidator : AbstractValidator<ChangeMyPasswordCommand>
{
    public ChangeMyPasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Informe a senha atual.");
        RuleFor(x => x.NewPassword).MinimumLength(12).WithMessage("A nova senha deve ter no mínimo 12 caracteres.");
        RuleFor(x => x.NewPassword).MaximumLength(72).WithMessage("A nova senha deve ter no máximo 72 caracteres.");
    }
}

internal sealed class ChangeMyPasswordHandler(IStaffUserRepository users, IPasswordHasher passwordHasher, IUnitOfWork unitOfWork)
    : ICommandHandler<ChangeMyPasswordCommand>
{
    public async Task<Result> Handle(ChangeMyPasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(new StaffUserId(request.UserId), cancellationToken);
        if (user is null || !passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            return IdentityApplicationErrors.CurrentPasswordWrong;
        }

        user.ChangePasswordHash(passwordHasher.Hash(request.NewPassword));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
