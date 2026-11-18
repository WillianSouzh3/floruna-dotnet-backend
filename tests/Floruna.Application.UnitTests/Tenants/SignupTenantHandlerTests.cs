using Floruna.Application.Abstractions;
using Floruna.Application.Tenants;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Floruna.Application.UnitTests.Tenants;

public sealed class SignupTenantHandlerTests
{
    private readonly TenantContext _tenantContext = new();
    private readonly ITenantRepository _tenants = Substitute.For<ITenantRepository>();
    private readonly IStaffUserRepository _users = Substitute.For<IStaffUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITenantResolver _resolver = Substitute.For<ITenantResolver>();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    public SignupTenantHandlerTests() => _hasher.Hash(Arg.Any<string>()).Returns("$hash");

    private SignupTenantHandler Handler() => new(_tenantContext, _tenants, _users, _hasher, _unitOfWork, _resolver, _clock);

    private static SignupTenantCommand Command(string slug = "jardim-secreto") =>
        new("Jardim Secreto", slug, "Maria Silva", "maria@jardim.com.br", "senha-bem-longa-123");

    [Fact]
    public async Task Creates_store_in_trial_and_its_master_user_in_one_save()
    {
        StaffUser? savedUser = null;
        _users.When(u => u.Add(Arg.Any<StaffUser>())).Do(call => savedUser = call.Arg<StaffUser>());

        var result = await Handler().Handle(Command(), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Tenant.Slug.ShouldBe("jardim-secreto");
        result.Value.Tenant.Plan.ShouldBe("free");
        result.Value.Tenant.EffectivePlan.ShouldBe("pro");
        result.Value.Tenant.InTrial.ShouldBeTrue();
        result.Value.Tenant.TrialEndsAt.ShouldBe("2026-12-02T12:00:00.000Z");

        savedUser.ShouldNotBeNull().Role.ShouldBe(Roles.Master);
        savedUser.TenantId.ToString().ShouldBe(result.Value.Tenant.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        _resolver.Received(1).Invalidate();
    }

    [Fact]
    public async Task Leaves_the_tenant_context_after_saving()
    {
        await Handler().Handle(Command(), default);

        _tenantContext.Tenant.ShouldBeNull();
    }

    [Fact]
    public async Task Reserved_slug_is_rejected_before_touching_the_database()
    {
        var result = await Handler().Handle(Command("admin"), default);

        result.Error.ShouldBe(TenantErrors.SlugReserved);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task Unique_violation_on_save_means_slug_taken()
    {
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new UniqueConstraintViolationException("ix_tenants_slug", new InvalidOperationException()));

        var result = await Handler().Handle(Command(), default);

        result.Error.ShouldBe(TenantErrors.SlugTaken);
        _resolver.DidNotReceive().Invalidate();
    }
}
