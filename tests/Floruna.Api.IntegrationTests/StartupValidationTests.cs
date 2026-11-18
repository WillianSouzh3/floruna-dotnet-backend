using Floruna.Api.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Floruna.Api.IntegrationTests;

/// <summary>Options Pattern com ValidateOnStart: configuração errada derruba a subida, não a 1ª requisição.</summary>
[Collection(ApiCollection.Name)]
public sealed class StartupValidationTests(FlorunaApiFactory factory)
{
    [Theory]
    [InlineData("ftp://loja.test")]
    [InlineData("http://loja.test/caminho")]
    [InlineData("nao-e-url")]
    public void Api_refuses_to_start_with_invalid_cors_origin(string origin)
    {
        using var broken = Override("Cors:AllowedOrigins:0", origin);

        Should.Throw<OptionsValidationException>(() => broken.CreateClient()).Message.ShouldContain(origin);
    }

    [Fact]
    public void Api_refuses_to_start_with_a_short_jwt_secret()
    {
        using var broken = Override("Jwt:Secret", "curto-demais");

        Should.Throw<OptionsValidationException>(() => broken.CreateClient()).Message.ShouldContain("32");
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Override(string key, string value) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));
}
