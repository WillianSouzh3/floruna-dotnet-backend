using Floruna.Domain.Identity;
using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.Identity;

public sealed class SingleUseTokenTests
{
    [Theory]
    [InlineData(SingleUseTokenPurpose.TwoFactorCode, 600)]
    [InlineData(SingleUseTokenPurpose.PasswordReset, 1800)]
    [InlineData(SingleUseTokenPurpose.Impersonation, 60)]
    public void Each_purpose_has_its_own_short_lifetime(SingleUseTokenPurpose purpose, int seconds)
    {
        var token = SingleUseToken.Issue(TenantId.New(), purpose, StaffUserId.New(), "hash", DateTimeOffset.UnixEpoch);

        token.ExpiresAt.ShouldBe(DateTimeOffset.UnixEpoch.AddSeconds(seconds));
        token.IsUsable(DateTimeOffset.UnixEpoch.AddSeconds(seconds - 1)).ShouldBeTrue();
        token.IsUsable(DateTimeOffset.UnixEpoch.AddSeconds(seconds)).ShouldBeFalse();
    }
}
