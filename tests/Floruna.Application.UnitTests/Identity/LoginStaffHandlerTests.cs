using Floruna.Application.Abstractions;
using Floruna.Application.Identity;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.SharedKernel;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Floruna.Application.UnitTests.Identity;

public sealed class LoginStaffHandlerTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private readonly TenantContext _tenantContext = new();
    private readonly IStaffUserRepository _users = Substitute.For<IStaffUserRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IAccessTokenIssuer _tokens = Substitute.For<IAccessTokenIssuer>();
    private readonly ILoginAttemptGuard _attempts = Substitute.For<ILoginAttemptGuard>();
    private readonly ISingleUseTokenRepository _singleUse = Substitute.For<ISingleUseTokenRepository>();
    private readonly ICredentialHasher _credentials = Substitute.For<ICredentialHasher>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly AuthenticationSettings _settings = new();
    private readonly StaffUser _maria = StaffUser.Create(
        Tenant, "Maria", Email.Create("maria@flora.com.br").Value, "$hash", Roles.Master, DateTimeOffset.UnixEpoch).Value;

    public LoginStaffHandlerTests()
    {
        _tenantContext.Set(new ResolvedTenant(Tenant, "flora-lis", TenantStatus.Active, TenantPlan.Pro));
        _tokens.IssueFor(Arg.Any<StaffUser>()).Returns("jwt");
        _tokens.IssueTwoFactorPending(Arg.Any<StaffUser>()).Returns("pending-jwt");
        _users.FindByEmailAsync(_maria.Email, Arg.Any<CancellationToken>()).Returns(_maria);
        _hasher.Verify("senha-certa", "$hash").Returns(true);
        _credentials.HashOneTimeCode(Arg.Any<StaffUserId>(), Arg.Any<string>()).Returns("code-hash");
    }

    private LoginStaffHandler Handler(ITenantContext? context = null) => new(
        context ?? _tenantContext, _users, _hasher, _tokens, _attempts, _singleUse, _credentials, _email, _unitOfWork,
        Options.Create(_settings), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task Correct_credentials_without_email_open_the_session_directly()
    {
        _email.IsEnabled.Returns(false); // sem SMTP, o 2FA fica desligado (senão ninguém entraria)

        var result = await Handler().Handle(new LoginStaffCommand("MARIA@flora.com.br", "senha-certa"), default);

        var session = result.Value.ShouldBeOfType<AuthResult>();
        session.AccessToken.ShouldBe("jwt");
        session.User.ShouldBe(new AuthUser(_maria.Id.ToString(), "Maria", "maria@flora.com.br", Roles.Master));
        await _attempts.Received(1).ClearAsync(LoginScope.Staff, "MARIA@flora.com.br", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_email_enabled_login_requires_the_second_factor()
    {
        _email.IsEnabled.Returns(true);

        var result = await Handler().Handle(new LoginStaffCommand("maria@flora.com.br", "senha-certa"), default);

        result.Value.ShouldBeOfType<PendingTwoFactorResult>().PendingToken.ShouldBe("pending-jwt");
        _tokens.DidNotReceive().IssueFor(Arg.Any<StaffUser>());
        _singleUse.Received(1).Add(Arg.Is<SingleUseToken>(t => t.Purpose == SingleUseTokenPurpose.TwoFactorCode && t.SecretHash == "code-hash"));
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To == "maria@flora.com.br"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Two_factor_can_be_turned_off_by_configuration()
    {
        _email.IsEnabled.Returns(true);
        _settings.TwoFactorEnabled = false;

        var result = await Handler().Handle(new LoginStaffCommand("maria@flora.com.br", "senha-certa"), default);

        result.Value.ShouldBeOfType<AuthResult>();
    }

    [Fact]
    public async Task Wrong_password_records_a_failure_and_returns_generic_error()
    {
        var result = await Handler().Handle(new LoginStaffCommand("maria@flora.com.br", "errada", "203.0.113.9"), default);

        result.Error.ShouldBe(IdentityErrors.InvalidCredentials);
        await _attempts.Received(1).RecordFailureAsync(LoginScope.Staff, "maria@flora.com.br", "203.0.113.9", Arg.Any<CancellationToken>());
        _tokens.DidNotReceive().IssueFor(Arg.Any<StaffUser>());
    }

    [Fact]
    public async Task Unknown_email_still_verifies_a_password_to_keep_timing_constant()
    {
        var result = await Handler().Handle(new LoginStaffCommand("ninguem@flora.com.br", "qualquer"), default);

        result.Error.ShouldBe(IdentityErrors.InvalidCredentials);
        _hasher.Received(1).Verify("qualquer", null);
    }

    [Fact]
    public async Task Locked_account_is_refused_before_checking_the_password()
    {
        _attempts.RemainingLockAsync(LoginScope.Staff, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(TimeSpan.FromSeconds(90.2));

        var result = await Handler().Handle(new LoginStaffCommand("maria@flora.com.br", "senha-certa"), default);

        result.Error.Type.ShouldBe(ErrorType.TooManyRequests);
        result.Error.RetryAfterSeconds.ShouldBe(91);
        _hasher.DidNotReceiveWithAnyArgs().Verify(default!, default);
    }

    [Fact]
    public async Task Without_resolved_store_login_is_refused()
    {
        var result = await Handler(new TenantContext()).Handle(new LoginStaffCommand("maria@flora.com.br", "senha-certa"), default);

        result.Error.ShouldBe(TenantErrors.NotResolved);
    }
}
