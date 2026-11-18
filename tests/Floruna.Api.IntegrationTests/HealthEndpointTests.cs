using System.Net;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;

namespace Floruna.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class HealthEndpointTests(FlorunaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("/api/v1/health/live")]
    [InlineData("/api/v1/health/ready")]
    [InlineData("/api/v1/health")]
    public async Task Health_endpoints_return_the_same_shape_as_the_nest_api(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = body.RootElement.GetProperty("data");
        data.GetProperty("status").GetString().ShouldBe("ok");
        data.GetProperty("checks").GetProperty("process").GetString().ShouldBe("up");
        data.GetProperty("version").GetString().ShouldBe("dev");
        data.GetProperty("uptime").GetDouble().ShouldBeGreaterThanOrEqualTo(0);
        data.TryGetProperty("db", out _).ShouldBeTrue();
        data.TryGetProperty("timestamp", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Readiness_checks_the_real_database()
    {
        var response = await _client.GetAsync("/api/v1/health/ready");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("data").GetProperty("db").GetString().ShouldBe("up");
    }
}
