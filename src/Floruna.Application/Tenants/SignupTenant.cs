using Floruna.Application.Abstractions;
using Floruna.Application.Messaging;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using FluentValidation;

namespace Floruna.Application.Tenants;

/// <summary>Cadastro self-service de loja (<c>POST /tenants/signup</c>). RF-PLT-01.</summary>
public sealed record SignupTenantCommand(string StoreName, string Slug, string AdminName, string Email, string Password)
    : ICommand<SignupResponse>;

internal sealed class SignupTenantValidator : AbstractValidator<SignupTenantCommand>
{
    public SignupTenantValidator()
    {
        RuleFor(x => x.StoreName).NotEmpty().MinimumLength(2).MaximumLength(80)
            .WithMessage("O nome da loja deve ter entre 2 e 80 caracteres.");
        RuleFor(x => x.Slug).NotEmpty().WithMessage("Informe o endereço da loja.");
        RuleFor(x => x.AdminName).NotEmpty().MinimumLength(2).MaximumLength(80)
            .WithMessage("O nome deve ter entre 2 e 80 caracteres.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("E-mail inválido.");
        RuleFor(x => x.Password).MinimumLength(12).WithMessage("A senha deve ter no mínimo 12 caracteres.");
        RuleFor(x => x.Password).MaximumLength(72).WithMessage("A senha deve ter no máximo 72 caracteres.");
    }
}

/// <summary>
/// Cria a loja E o usuário master numa ÚNICA transação (um SaveChanges).
/// </summary>
/// <remarks>
/// <para>Exceção consciente à regra "uma transação, um agregado": uma loja sem
/// dono é um estado inválido que ninguém consegue consertar pelo produto.
/// O Nest fazia em dois passos e apagava a loja se o usuário falhasse
/// (compensação manual); aqui o banco garante o tudo-ou-nada.</para>
/// <para>Depende da classe concreta <see cref="TenantContext"/> (e não só da
/// interface) porque precisa ENTRAR no contexto da loja recém-criada.</para>
/// </remarks>
internal sealed class SignupTenantHandler(
    TenantContext tenantContext,
    ITenantRepository tenants,
    IStaffUserRepository users,
    IPasswordHasher passwordHasher,
    IUnitOfWork unitOfWork,
    ITenantResolver tenantResolver,
    TimeProvider clock) : ICommandHandler<SignupTenantCommand, SignupResponse>
{
    public async Task<Result<SignupResponse>> Handle(SignupTenantCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var created = Tenant.Create(request.StoreName, request.Slug, now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var email = Email.Create(request.Email);
        if (email.IsFailure)
        {
            return email.Error;
        }

        var tenant = created.Value;
        tenant.StartTrial(now);

        var master = StaffUser.Create(tenant.Id, request.AdminName, email.Value, passwordHasher.Hash(request.Password), Roles.Master, now);
        if (master.IsFailure)
        {
            return master.Error;
        }

        tenants.Add(tenant);
        using (tenantContext.Enter(new ResolvedTenant(tenant.Id, tenant.Slug.Value, tenant.Status, tenant.EffectivePlan(now))))
        {
            users.Add(master.Value);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException)
            {
                // Índice único no slug: dois cadastros simultâneos com o mesmo endereço.
                return TenantErrors.SlugTaken;
            }
        }

        tenantResolver.Invalidate();
        return new SignupResponse(TenantResponse.From(tenant, now));
    }
}
