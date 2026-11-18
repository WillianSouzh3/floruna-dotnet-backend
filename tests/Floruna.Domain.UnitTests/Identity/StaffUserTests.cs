using Floruna.Domain.Identity;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Identity;

public sealed class StaffUserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly Email Email = Email.Create("maria@flora.com.br").Value;

    private static StaffUser NewUser(string role = Roles.Master) =>
        StaffUser.Create(TenantId.New(), "Maria", Email, "$2a$10$hash", role, Now).Value;

    [Fact]
    public void New_user_starts_with_session_version_zero()
    {
        NewUser().SessionVersion.ShouldBe(0);
    }

    [Fact]
    public void Revoking_sessions_increments_the_version()
    {
        var user = NewUser();

        user.RevokeSessions();
        user.RevokeSessions();

        user.SessionVersion.ShouldBe(2);
    }

    [Fact]
    public void Changing_password_revokes_open_sessions()
    {
        var user = NewUser();

        user.ChangePasswordHash("$2a$10$other");

        user.PasswordHash.ShouldBe("$2a$10$other");
        user.SessionVersion.ShouldBe(1);
    }

    [Theory]
    [InlineData(Roles.Customer)]
    [InlineData("admin")]
    public void Only_staff_roles_are_accepted(string role)
    {
        StaffUser.Create(TenantId.New(), "Maria", Email, "$2a$10$hash", role, Now).Error.ShouldBe(IdentityErrors.RoleInvalid);
    }
}
