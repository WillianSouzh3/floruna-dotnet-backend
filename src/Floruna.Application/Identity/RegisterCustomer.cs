using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Customers;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Identity;

/// <summary>Cadastro de cliente na vitrine (<c>POST /auth/register</c>). Já devolve autenticado.</summary>
public sealed record RegisterCustomerCommand(string Name, string Email, string Password, string? Phone)
    : ICommand<AuthResult>;

internal sealed class RegisterCustomerValidator : AbstractValidator<RegisterCustomerCommand>
{
    public RegisterCustomerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MinimumLength(2).MaximumLength(80)
            .WithMessage("O nome deve ter entre 2 e 80 caracteres.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("E-mail inválido.");
        RuleFor(x => x.Password).MinimumLength(8).WithMessage("A senha deve ter ao menos 8 caracteres.");
        RuleFor(x => x.Password).MaximumLength(72).WithMessage("A senha deve ter no máximo 72 caracteres.");
    }
}

internal sealed class RegisterCustomerHandler(
    ITenantContext tenantContext,
    ICustomerRepository customers,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer tokens,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : ICommandHandler<RegisterCustomerCommand, AuthResult>
{
    public async Task<Result<AuthResult>> Handle(RegisterCustomerCommand request, CancellationToken cancellationToken)
    {
        if (tenantContext.Tenant is not { } tenant)
        {
            return TenantErrors.NotResolved;
        }

        var email = Email.Create(request.Email);
        if (email.IsFailure)
        {
            return email.Error;
        }

        var registered = Customer.Register(
            tenant.Id, request.Name, email.Value, passwordHasher.Hash(request.Password), request.Phone, clock.GetUtcNow());
        if (registered.IsFailure)
        {
            return registered.Error;
        }

        customers.Add(registered.Value);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // O índice único (tenant, email) é a verificação de existência: um
            // "SELECT antes" deixaria uma janela de corrida entre dois cadastros.
            return IdentityErrors.EmailTaken;
        }

        var customer = registered.Value;
        return new AuthResult(
            tokens.IssueFor(customer),
            new AuthUser(customer.Id.ToString(), customer.Name, customer.Email.Value, Roles.Customer));
    }
}
