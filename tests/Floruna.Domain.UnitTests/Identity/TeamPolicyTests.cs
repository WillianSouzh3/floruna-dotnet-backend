using Floruna.Domain.Identity;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Identity;

public sealed class TeamPolicyTests
{
    private static readonly TenantId Store = TenantId.New();

    private static StaffUser Member(string role, string email) =>
        StaffUser.Create(Store, "Pessoa", Email.Create(email).Value, "$hash", role, DateTimeOffset.UnixEpoch).Value;

    [Fact]
    public void The_last_owner_cannot_be_demoted_or_removed()
    {
        var owner = Member(Roles.Master, "dona@loja.com.br");
        var other = StaffUserId.New();

        TeamPolicy.CanChangeRole(owner, Roles.Manager, other, masterCount: 1).Error.ShouldBe(TeamPolicy.LastOwner);
        TeamPolicy.CanRemove(owner, other, masterCount: 1).Error.ShouldBe(TeamPolicy.LastOwner);
    }

    [Fact]
    public void With_two_owners_one_can_be_demoted_by_the_other()
    {
        var owner = Member(Roles.Master, "dona@loja.com.br");

        TeamPolicy.CanChangeRole(owner, Roles.Manager, StaffUserId.New(), masterCount: 2).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Nobody_demotes_or_removes_themselves()
    {
        var owner = Member(Roles.Master, "dona@loja.com.br");

        TeamPolicy.CanChangeRole(owner, Roles.Manager, owner.Id, masterCount: 3).Error.ShouldBe(TeamPolicy.CannotDemoteSelf);
        TeamPolicy.CanRemove(owner, owner.Id, masterCount: 3).Error.ShouldBe(TeamPolicy.CannotRemoveSelf);
    }

    [Fact]
    public void Changing_role_revokes_sessions_and_rejects_platform_roles()
    {
        var cashier = Member(Roles.Cashier, "caixa@loja.com.br");

        cashier.ChangeRole(Roles.Manager).IsSuccess.ShouldBeTrue();
        cashier.SessionVersion.ShouldBe(1);
        cashier.ChangeRole(Roles.SuperAdmin).Error.ShouldBe(IdentityErrors.RoleInvalid);
    }
}
