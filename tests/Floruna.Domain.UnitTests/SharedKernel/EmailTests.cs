using Floruna.SharedKernel;

namespace Floruna.Domain.UnitTests.SharedKernel;

public sealed class EmailTests
{
    [Fact]
    public void Email_is_normalized_to_lowercase_without_spaces()
    {
        Email.Create("  Maria@Flora.COM.br ").Value.Value.ShouldBe("maria@flora.com.br");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maria")]
    [InlineData("maria@")]
    [InlineData("Maria <maria@flora.com.br>")]
    public void Invalid_emails_are_rejected(string? raw)
    {
        Email.Create(raw).IsFailure.ShouldBeTrue();
    }
}
