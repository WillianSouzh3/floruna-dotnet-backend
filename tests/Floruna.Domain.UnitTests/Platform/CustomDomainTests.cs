using Floruna.Domain.Platform;

namespace Floruna.Domain.UnitTests.Platform;

public sealed class CustomDomainTests
{
    private const string Platform = "floruna.com.br";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData("www.minhaflora.com.br", "www.minhaflora.com.br")]
    [InlineData("https://MinhaFlora.com.br/loja", "minhaflora.com.br")]
    [InlineData("minhaflora.com.br:443", "minhaflora.com.br")]
    [InlineData("minhaflora.com.br.", "minhaflora.com.br")]
    public void Domain_is_normalized(string raw, string expected)
    {
        CustomDomainName.Create(raw, Platform).Value.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("minhaflora")]
    [InlineData("-x.com.br")]
    [InlineData("loja.floruna.com.br")]
    [InlineData("floruna.com.br")]
    [InlineData("minha.loja.test")]
    [InlineData("intranet.local")]
    public void Invalid_platform_or_reserved_domains_are_rejected(string raw)
    {
        CustomDomainName.Create(raw, Platform).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Txt_record_goes_on_the_apex_domain()
    {
        CustomDomainName.VerificationRecordFor("www.loja.com.br").ShouldBe("_flora-verify.loja.com.br");
        CustomDomainName.VerificationRecordFor("loja.com.br").ShouldBe("_flora-verify.loja.com.br");
    }

    [Fact]
    public void Domain_is_verified_only_when_the_published_txt_matches()
    {
        var tenant = Tenant.Create("Loja", "loja", Now).Value;
        tenant.RequestCustomDomain(CustomDomainName.Create("www.loja.com.br", Platform).Value, "flora-verify=abc");

        tenant.VerifyCustomDomain(["outra-coisa"], Now).Error.Code.ShouldBe("VALIDATION_ERROR");
        tenant.CustomDomainVerified.ShouldBeFalse();

        tenant.VerifyCustomDomain(["v=spf1", "flora-verify=abc"], Now).IsSuccess.ShouldBeTrue();
        tenant.CustomDomainVerified.ShouldBeTrue();
        tenant.CustomDomainVerifiedAt.ShouldBe(Now);
    }

    [Fact]
    public void Requesting_a_new_domain_resets_the_verification()
    {
        var tenant = Tenant.Create("Loja", "loja", Now).Value;
        tenant.RequestCustomDomain(CustomDomainName.Create("loja.com.br", Platform).Value, "flora-verify=1");
        tenant.VerifyCustomDomain(["flora-verify=1"], Now);

        tenant.RequestCustomDomain(CustomDomainName.Create("outra.com.br", Platform).Value, "flora-verify=2");

        tenant.CustomDomainVerified.ShouldBeFalse();
        tenant.CustomDomainToken.ShouldBe("flora-verify=2");
    }

    [Fact]
    public void Verifying_without_request_fails()
    {
        Tenant.Create("Loja", "loja", Now).Value.VerifyCustomDomain(["x"], Now).Error.ShouldBe(CustomDomainErrors.NothingPending);
    }
}
