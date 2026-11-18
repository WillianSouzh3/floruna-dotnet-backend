using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Customers;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Identity;

/// <summary>Login de cliente na vitrine (<c>POST /auth/login</c>).</summary>
public sealed record LoginCustomerCommand(string Email, string Password, string? ClientIp = null) : ICommand<AuthResult>;

internal sealed class LoginCustomerValidator : AbstractValidator<LoginCustomerCommand>
{
    public LoginCustomerValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("E-mail inválido.");
        RuleFor(x => x.Password).MinimumLength(6).WithMessage("A senha deve ter ao menos 6 caracteres.");
    }
}

internal sealed class LoginCustomerHandler(
    ITenantContext tenantContext,
    ICustomerRepository customers,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer tokens,
    ILoginAttemptGuard attempts) : ICommandHandler<LoginCustomerCommand, AuthResult>
{
    public async Task<Result<AuthResult>> Handle(LoginCustomerCommand request, CancellationToken cancellationToken)
    {
        if (tenantContext.Tenant is null)
        {
            return TenantErrors.NotResolved;
        }

        var remaining = await attempts.RemainingLockAsync(LoginScope.Customer, request.Email, request.ClientIp, cancellationToken);
        if (remaining > TimeSpan.Zero)
        {
            return IdentityApplicationErrors.Locked(remaining);
        }

        var email = Email.Create(request.Email);
        var customer = email.IsSuccess ? await customers.FindByEmailAsync(email.Value, cancellationToken) : null;

        if (!passwordHasher.Verify(request.Password, customer?.PasswordHash) || customer is null)
        {
            await attempts.RecordFailureAsync(LoginScope.Customer, request.Email, request.ClientIp, cancellationToken);
            return IdentityErrors.InvalidCredentials;
        }

        await attempts.ClearAsync(LoginScope.Customer, request.Email, cancellationToken);

        return new AuthResult(
            tokens.IssueFor(customer),
            new AuthUser(customer.Id.ToString(), customer.Name, customer.Email.Value, Roles.Customer));
    }
}
