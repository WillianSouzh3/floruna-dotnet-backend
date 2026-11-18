using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Floruna.Application.Identity;

/// <summary>Login da equipe no painel (<c>POST /auth/admin/login</c>).</summary>
/// <param name="ClientIp">Preenchido pelo controller (nunca pelo corpo da requisição).</param>
public sealed record LoginStaffCommand(string Email, string Password, string? ClientIp = null) : ICommand<IStaffLoginOutcome>;

internal sealed class LoginStaffValidator : AbstractValidator<LoginStaffCommand>
{
    public LoginStaffValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("E-mail inválido.");
        RuleFor(x => x.Password).MinimumLength(6).WithMessage("A senha deve ter ao menos 6 caracteres.");
    }
}

/// <summary>
/// Fluxo: bloqueio? → confere senha (tempo constante) → registra falha ou limpa
/// → 2FA (se houver e-mail) ou sessão direta.
/// </summary>
internal sealed class LoginStaffHandler(
    ITenantContext tenantContext,
    IStaffUserRepository users,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer tokens,
    ILoginAttemptGuard attempts,
    ISingleUseTokenRepository singleUseTokens,
    ICredentialHasher credentialHasher,
    IEmailSender email,
    IUnitOfWork unitOfWork,
    IOptions<AuthenticationSettings> settings,
    TimeProvider clock) : ICommandHandler<LoginStaffCommand, IStaffLoginOutcome>
{
    public async Task<Result<IStaffLoginOutcome>> Handle(LoginStaffCommand request, CancellationToken cancellationToken)
    {
        if (tenantContext.Tenant is not { } tenant)
        {
            return TenantErrors.NotResolved;
        }

        var remaining = await attempts.RemainingLockAsync(LoginScope.Staff, request.Email, request.ClientIp, cancellationToken);
        if (remaining > TimeSpan.Zero)
        {
            return IdentityApplicationErrors.Locked(remaining);
        }

        var parsedEmail = Email.Create(request.Email);
        var user = parsedEmail.IsSuccess ? await users.FindByEmailAsync(parsedEmail.Value, cancellationToken) : null;

        // Sempre roda o Verify, mesmo sem usuário: tempo constante (anti-enumeração).
        if (!passwordHasher.Verify(request.Password, user?.PasswordHash) || user is null)
        {
            await attempts.RecordFailureAsync(LoginScope.Staff, request.Email, request.ClientIp, cancellationToken);
            return IdentityErrors.InvalidCredentials;
        }

        await attempts.ClearAsync(LoginScope.Staff, request.Email, cancellationToken);

        if (!settings.Value.TwoFactorEnabled || !email.IsEnabled)
        {
            return AuthResults.For(user, tokens.IssueFor(user));
        }

        // 2FA: código de 6 dígitos, guardado como HMAC, válido por 10 min. Pedir de novo invalida o anterior.
        var code = SecureRandom.SixDigitCode();
        await singleUseTokens.RevokePendingAsync(user.Id, SingleUseTokenPurpose.TwoFactorCode, cancellationToken);
        singleUseTokens.Add(SingleUseToken.Issue(
            tenant.Id, SingleUseTokenPurpose.TwoFactorCode, user.Id, credentialHasher.HashOneTimeCode(user.Id, code), clock.GetUtcNow()));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await email.SendAsync(
            new EmailMessage(
                user.Email.Value,
                "Seu código de acesso",
                $"Olá, {user.Name}.\n\nSeu código de acesso ao painel da loja {tenant.Slug} é: {code}\n\n" +
                "Ele vale por 10 minutos. Se não foi você que tentou entrar, troque sua senha."),
            cancellationToken);

        return new PendingTwoFactorResult(tokens.IssueTwoFactorPending(user));
    }
}

/// <summary>Segunda etapa do 2FA (<c>POST /auth/admin/verify-otp</c>).</summary>
public sealed record VerifyTwoFactorCommand(string PendingToken, string Code) : ICommand<AuthResult>;

internal sealed class VerifyTwoFactorValidator : AbstractValidator<VerifyTwoFactorCommand>
{
    public VerifyTwoFactorValidator()
    {
        RuleFor(x => x.PendingToken).NotEmpty().WithMessage("Token expirado ou inválido.");
        RuleFor(x => x.Code).Matches("^[0-9]{6}$").WithMessage("Informe o código de 6 dígitos.");
    }
}

internal sealed class VerifyTwoFactorHandler(
    IAccessTokenIssuer tokens,
    ISingleUseTokenRepository singleUseTokens,
    ICredentialHasher credentialHasher,
    IStaffUserRepository users,
    TimeProvider clock) : ICommandHandler<VerifyTwoFactorCommand, AuthResult>
{
    public async Task<Result<AuthResult>> Handle(VerifyTwoFactorCommand request, CancellationToken cancellationToken)
    {
        if (tokens.ReadTwoFactorPending(request.PendingToken) is not { } userId)
        {
            return IdentityApplicationErrors.PendingTokenInvalid;
        }

        // Busca e consome numa operação condicional: o mesmo código não serve duas vezes.
        var consumed = await singleUseTokens.TryConsumeAsync(
            SingleUseTokenPurpose.TwoFactorCode, credentialHasher.HashOneTimeCode(userId, request.Code), clock.GetUtcNow(), cancellationToken);
        if (consumed is null || consumed.UserId != userId)
        {
            return IdentityApplicationErrors.CodeInvalid;
        }

        var user = await users.GetByIdAsync(userId, cancellationToken);
        return user is null ? IdentityErrors.UserNotFound : AuthResults.For(user, tokens.IssueFor(user));
    }
}
