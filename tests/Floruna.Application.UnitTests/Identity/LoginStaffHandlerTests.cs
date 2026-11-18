using Floruna.Application.Abstractions;
using Floruna.Application.Identity;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using NSubstitute;

namespace Floruna.Application.UnitTests.Identity;

public sealed class LoginStaffHandlerTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private readonly TenantContext _tenantContext = new();
    private readonly IStaffUserRepository _users = Substitute.For<IStaffUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IAccessTokenIssuer _tokens = Substitute.For<IAccessTokenIssuer>();

    public LoginStaffHandlerTests()
    {
        _tenantContext.Set(new ResolvedTenant(Tenant, "flora-lis", TenantStatus.Active, TenantPlan.Pro));
        _tokens.IssueFor(Arg.Any<StaffUser>()).Returns("jwt");
    }

    private LoginStaffHandler Handler() => new(_tenantContext, _users, _hasher, _tokens);

    private static StaffUser Maria() =>
        StaffUser.Create(Tenant, "Maria", Email.Create("maria@flora.com.br").Value, "$hash", Roles.Master, DateTimeOffset.UnixEpoch).Value;

    [Fact]
    public async Task Correct_credentials_return_token_and_user()
    {
        var maria = Maria();
        _users.FindByEmailAsync(maria.Email, Arg.Any<CancellationToken>()).Returns(maria);
        _hasher.Verify("senha-certa", "$hash").Returns(true);

        var result = await Handler().Handle(new LoginStaffCommand("MARIA@flora.com.br", "senha-certa"), default);

        result.Value.AccessToken.ShouldBe("jwt");
        result.Value.User.ShouldBe(new AuthUser(maria.Id.ToString(), "Maria", "maria@flora.com.br", Roles.Master));
    }

    [Fact]
    public async Task Wrong_password_returns_generic_invalid_credentials()
    {
        var maria = Maria();
        _users.FindByEmailAsync(maria.Email, Arg.Any<CancellationToken>()).Returns(maria);
        _hasher.Verify(Arg.Any<string>(), Arg.Any<string?>()).Returns(false);

        var result = await Handler().Handle(new LoginStaffCommand("maria@flora.com.br", "errada"), default);

        result.Error.ShouldBe(IdentityErrors.InvalidCredentials);
        _tokens.DidNotReceiveWithAnyArgs().IssueFor(default(StaffUser)!);
    }

    [Fact]
    public async Task Unknown_email_still_verifies_a_password_to_keep_timing_constant()
    {
        _users.FindByEmailAsync(Arg.Any<Email>(), Arg.Any<CancellationToken>()).Returns((StaffUser?)null);

        var result = await Handler().Handle(new LoginStaffCommand("ninguem@flora.com.br", "qualquer"), default);

        result.Error.ShouldBe(IdentityErrors.InvalidCredentials);
        _hasher.Received(1).Verify("qualquer", null);
    }

    [Fact]
    public async Task Without_resolved_store_login_is_refused()
    {
        var handler = new LoginStaffHandler(new TenantContext(), _users, _hasher, _tokens);

        var result = await handler.Handle(new LoginStaffCommand("maria@flora.com.br", "senha-certa"), default);

        result.Error.ShouldBe(TenantErrors.NotResolved);
    }
}
