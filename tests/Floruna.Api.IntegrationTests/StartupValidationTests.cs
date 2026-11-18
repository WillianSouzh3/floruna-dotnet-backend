using Floruna.Api.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Floruna.Api.IntegrationTests;

/// <summary>Options Pattern com ValidateOnStart: configuração errada derruba a subida, não a 1ª requisição.</summary>
public sealed class StartupValidationTests
{
    [Theory]
    [InlineData("ftp://loja.test")]
    [InlineData("http://loja.test/caminho")]
    [InlineData("nao-e-url")]
    public void Api_refuses_to_start_with_invalid_cors_origin(string origin)
    {
        using var factory = new FlorunaApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = origin,
            })));

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        exception.Message.ShouldContain(origin);
    }
}
