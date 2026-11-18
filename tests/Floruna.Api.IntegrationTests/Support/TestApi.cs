using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Floruna.Api.IntegrationTests.Support;

/// <summary>Atalhos para os testes falarem com a API como o BFF Next.js fala.</summary>
public static class TestApi
{
    public const string StrongPassword = "senha-bem-longa-123";

    public static string NewSlug() => $"loja-{Guid.NewGuid():N}"[..20];

    /// <summary>Cliente "visitando" a loja: o BFF manda o host em x-tenant-host.</summary>
    public static HttpClient ForStore(this FlorunaApiFactory factory, string slug, string? token = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-tenant-host", $"{slug}.localhost:3000");
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public static async Task<(string Slug, string Email)> SignupStoreAsync(this FlorunaApiFactory factory, string? slug = null)
    {
        var (s, e, _) = await factory.SignupStoreWithIdAsync(slug);
        return (s, e);
    }

    public static async Task<(string Slug, string Email, Guid TenantId)> SignupStoreWithIdAsync(this FlorunaApiFactory factory, string? slug = null)
    {
        slug ??= NewSlug();
        var email = $"dona@{slug}.com.br";
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/tenants/signup", new
        {
            storeName = "Loja de Teste",
            slug,
            adminName = "Dona da Loja",
            email,
            password = StrongPassword,
        });
        response.EnsureSuccessStatusCode();
        using var body = await response.ReadJsonAsync();
        return (slug, email, Guid.Parse(body.Data().GetProperty("tenant").GetProperty("id").GetString()!));
    }

    public static async Task<string> LoginStaffAsync(this FlorunaApiFactory factory, string slug, string email)
    {
        var response = await factory.ForStore(slug).PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = StrongPassword });
        response.EnsureSuccessStatusCode();
        using var body = await response.ReadJsonAsync();
        return body.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    public static async Task<JsonDocument> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    public static JsonElement Data(this JsonDocument document) => document.RootElement.GetProperty("data");

    public static JsonElement ErrorBody(this JsonDocument document) => document.RootElement.GetProperty("error");

    /// <summary>Lê o payload do JWT (sem validar a assinatura: só para inspecionar claims).</summary>
    public static JsonDocument JwtPayload(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
    }
}
