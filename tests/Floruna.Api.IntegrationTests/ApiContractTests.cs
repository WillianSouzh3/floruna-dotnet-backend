using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;

namespace Floruna.Api.IntegrationTests;

/// <summary>
/// O contrato que o front Next.js espera (o mesmo do NestJS atual).
/// Quebrar qualquer um destes testes = quebrar o front.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ApiContractTests(FlorunaApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Success_is_wrapped_in_data_and_meta()
    {
        var response = await _client.GetAsync("/api/v1/_probe/ok");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await ReadJson(response);
        body.RootElement.GetProperty("data").GetProperty("name").GetString().ShouldBe("Rosa");
        body.RootElement.GetProperty("data").GetProperty("priceCents").GetInt64().ShouldBe(1_990);
        AssertMeta(body.RootElement.GetProperty("meta"), response);
    }

    [Fact]
    public async Task Business_error_uses_error_envelope_with_stable_code()
    {
        var response = await _client.GetAsync("/api/v1/_probe/conflict");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = await ReadJson(response);
        var error = body.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("COUPON_EXHAUSTED");
        error.GetProperty("message").GetString().ShouldBe("O cupom se esgotou.");
        error.TryGetProperty("issues", out _).ShouldBeFalse();
        body.RootElement.TryGetProperty("data", out _).ShouldBeFalse();
        AssertMeta(body.RootElement.GetProperty("meta"), response);
    }

    [Fact]
    public async Task Validation_error_lists_issues()
    {
        var response = await _client.GetAsync("/api/v1/_probe/invalid");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ReadJson(response);
        var error = body.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("VALIDATION_ERROR");
        error.GetProperty("issues").EnumerateArray().Select(i => i.GetString()).ShouldBe(["Nome é obrigatório."]);
    }

    [Fact]
    public async Task Invalid_request_body_returns_validation_envelope()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/_probe/flowers", new { priceCents = 0 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await ReadJson(response);
        var error = body.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("VALIDATION_ERROR");
        error.GetProperty("message").GetString().ShouldBe("Dados inválidos.");
        var issues = error.GetProperty("issues").EnumerateArray().Select(i => i.GetString()).ToList();
        issues.ShouldContain("Nome é obrigatório.");
        issues.ShouldContain("Preço deve ser positivo.");
    }

    [Fact]
    public async Task Valid_request_body_keeps_status_code_and_is_wrapped()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/_probe/flowers", new { name = "Lírio", priceCents = 3_500 });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var body = await ReadJson(response);
        body.RootElement.GetProperty("data").GetProperty("name").GetString().ShouldBe("Lírio");
    }

    [Fact]
    public async Task Unhandled_exception_returns_500_without_leaking_details()
    {
        var response = await _client.GetAsync("/api/v1/_probe/boom");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var raw = await response.Content.ReadAsStringAsync();
        raw.ShouldNotContain("detalhe interno");
        using var body = JsonDocument.Parse(raw);
        body.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("INTERNAL_ERROR");
        body.RootElement.GetProperty("error").GetProperty("message").GetString().ShouldBe("Erro interno do servidor.");
    }

    [Fact]
    public async Task Unknown_route_returns_not_found_envelope()
    {
        var response = await _client.GetAsync("/api/v1/does-not-exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var body = await ReadJson(response);
        body.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("NOT_FOUND");
        AssertMeta(body.RootElement.GetProperty("meta"), response);
    }

    [Fact]
    public async Task Safe_incoming_request_id_is_propagated()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_probe/ok");
        request.Headers.Add("X-Request-ID", "bff-123.abc");

        var response = await _client.SendAsync(request);

        response.Headers.GetValues("X-Request-ID").Single().ShouldBe("bff-123.abc");
        using var body = await ReadJson(response);
        body.RootElement.GetProperty("meta").GetProperty("requestId").GetString().ShouldBe("bff-123.abc");
    }

    [Fact]
    public async Task Unsafe_incoming_request_id_is_replaced()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/_probe/ok");
        request.Headers.TryAddWithoutValidation("X-Request-ID", "x\" OR 1=1 <script>");

        var response = await _client.SendAsync(request);

        var id = response.Headers.GetValues("X-Request-ID").Single();
        Guid.TryParse(id, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Responses_carry_version_and_security_headers()
    {
        var response = await _client.GetAsync("/api/v1/_probe/ok");

        response.Headers.GetValues("X-API-Version").Single().ShouldBe("1");
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
    }

    [Fact]
    public async Task Cors_allows_only_the_configured_frontend_origin()
    {
        using var allowed = Preflight(FlorunaApiFactory.AllowedOrigin);
        using var denied = Preflight("http://evil.test");

        var allowedResponse = await _client.SendAsync(allowed);
        var deniedResponse = await _client.SendAsync(denied);

        allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(FlorunaApiFactory.AllowedOrigin);
        deniedResponse.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task OpenApi_document_is_published()
    {
        var response = await _client.GetAsync("/api/openapi/v1.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("/api/v1/health/live");
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/_probe/ok");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static void AssertMeta(JsonElement meta, HttpResponseMessage response)
    {
        meta.GetProperty("version").GetString().ShouldBe("1");
        meta.GetProperty("requestId").GetString().ShouldBe(response.Headers.GetValues("X-Request-ID").Single());
        // Mesmo formato do Date.toISOString(): 2026-11-18T13:45:07.123Z
        meta.GetProperty("timestamp").GetString()!.ShouldMatch(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");
    }
}
