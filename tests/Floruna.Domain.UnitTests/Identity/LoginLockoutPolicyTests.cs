using Floruna.Domain.Identity;

namespace Floruna.Domain.UnitTests.Identity;

public sealed class LoginLockoutPolicyTests
{
    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(6, 5)]
    [InlineData(7, 15)]
    [InlineData(8, 60)]
    [InlineData(50, 60)]
    public void Account_and_ip_locks_early_and_grows(int failures, int minutes)
    {
        LoginLockoutPolicy.LockForAccountAndIp(failures).ShouldBe(TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(15, 5)]
    [InlineData(20, 15)]
    [InlineData(30, 60)]
    public void Account_from_any_ip_tolerates_more_before_locking(int failures, int minutes)
    {
        LoginLockoutPolicy.LockForAccount(failures).ShouldBe(TimeSpan.FromMinutes(minutes));
    }
}
