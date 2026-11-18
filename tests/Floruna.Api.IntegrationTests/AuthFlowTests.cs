using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Api.Security;
using Floruna.Application.Abstractions;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Fase 1 de ponta a ponta: cadastro de loja, login, sessão, revogação, papéis e planos.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthFlowTests(FlorunaApiFactory factory)
{
    // ── Cadastro de loja ─────────────────────────────────────────────────

    [Fact]
    public async Task Signup_creates_store_in_pro_trial()
    {
        var slug = TestApi.NewSlug();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/tenants/signup", new
        {
            storeName = "Jardim Secreto",
            slug,
            adminName = "Maria",
            email = "maria@jardim.com.br",
            password = TestApi.StrongPassword,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var body = await response.ReadJsonAsync();
        var tenant = body.Data().GetProperty("tenant");
        tenant.GetProperty("slug").GetString().ShouldBe(slug);
        tenant.GetProperty("status").GetString().ShouldBe("active");
        tenant.GetProperty("plan").GetString().ShouldBe("free");
        tenant.GetProperty("effectivePlan").GetString().ShouldBe("pro");
        tenant.GetProperty("inTrial").GetBoolean().ShouldBeTrue();
        tenant.GetProperty("customDomainStatus").GetString().ShouldBe("pending");
    }

    [Fact]
    public async Task Signup_with_taken_slug_returns_conflict()
    {
        var (slug, _) = await factory.SignupStoreAsync();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/tenants/signup", new
        {
            storeName = "Outra",
            slug,
            adminName = "João",
            email = "joao@outra.com.br",
            password = TestApi.StrongPassword,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = await response.ReadJsonAsync();
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("Este endereço de loja já está em uso.");
    }

    [Fact]
    public async Task Signup_with_short_password_lists_validation_issues()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/tenants/signup", new
        {
            storeName = "Loja",
            slug = TestApi.NewSlug(),
            adminName = "Maria",
            email = "maria@loja.com.br",
            password = "curta",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await response.ReadJsonAsync();
        body.ErrorBody().GetProperty("issues").EnumerateArray().Select(i => i.GetString())
            .ShouldContain("A senha deve ter no mínimo 12 caracteres.");
    }

    // ── Login da equipe e JWT compatível com o Nest ──────────────────────

    [Fact]
    public async Task Staff_login_issues_a_token_with_the_nest_claims()
    {
        var (slug, email) = await factory.SignupStoreAsync();

        var response = await factory.ForStore(slug).PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = TestApi.StrongPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        var data = body.Data();
        data.GetProperty("user").GetProperty("role").GetString().ShouldBe("master");
        data.GetProperty("user").GetProperty("email").GetString().ShouldBe(email);

        using var payload = TestApi.JwtPayload(data.GetProperty("accessToken").GetString()!);
        var claims = payload.RootElement;
        claims.GetProperty("sub").GetString().ShouldBe(data.GetProperty("user").GetProperty("id").GetString());
        claims.GetProperty("role").GetString().ShouldBe("master");
        claims.GetProperty("email").GetString().ShouldBe(email);
        claims.GetProperty("tenantId").GetString().ShouldNotBeNullOrEmpty();
        claims.GetProperty("sessionVersion").ValueKind.ShouldBe(JsonValueKind.Number);
        (claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64()).ShouldBe(8 * 3600);
    }

    [Theory]
    [InlineData("senha-errada-123")]
    [InlineData(null)]
    public async Task Wrong_password_or_unknown_email_get_the_same_answer(string? password)
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var credentials = password is null
            ? new { email = "ninguem@lugar.com.br", password = TestApi.StrongPassword }
            : new { email, password };

        var response = await factory.ForStore(slug).PostAsJsonAsync("/api/v1/auth/admin/login", credentials);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var body = await response.ReadJsonAsync();
        body.ErrorBody().GetProperty("code").GetString().ShouldBe("AUTHENTICATION_REQUIRED");
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("E-mail ou senha incorretos.");
    }

    [Fact]
    public async Task Me_returns_the_session_user()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var token = await factory.LoginStaffAsync(slug, email);

        var response = await factory.ForStore(slug, token).GetAsync("/api/v1/auth/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        var user = body.Data().GetProperty("user");
        user.GetProperty("email").GetString().ShouldBe(email);
        user.GetProperty("role").GetString().ShouldBe("master");
        user.GetProperty("sessionVersion").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Me_without_token_requires_authentication()
    {
        var (slug, _) = await factory.SignupStoreAsync();

        var response = await factory.ForStore(slug).GetAsync("/api/v1/auth/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var body = await response.ReadJsonAsync();
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("Autenticação necessária.");
    }

    [Fact]
    public async Task Token_of_one_store_is_rejected_in_another_store()
    {
        var (storeA, emailA) = await factory.SignupStoreAsync();
        var (storeB, _) = await factory.SignupStoreAsync();
        var tokenOfA = await factory.LoginStaffAsync(storeA, emailA);

        var response = await factory.ForStore(storeB, tokenOfA).GetAsync("/api/v1/auth/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var body = await response.ReadJsonAsync();
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("Sessão não pertence a esta loja.");
    }

    [Fact]
    public async Task Revoking_sessions_invalidates_previously_issued_tokens()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var oldToken = await factory.LoginStaffAsync(slug, email);

        var revoke = await factory.ForStore(slug, oldToken).PostAsync("/api/v1/auth/admin/revoke-sessions", null);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var revoked = await revoke.ReadJsonAsync())
        {
            revoked.Data().GetProperty("ok").GetBoolean().ShouldBeTrue();
        }

        var withOldToken = await factory.ForStore(slug, oldToken).GetAsync("/api/v1/auth/me");
        withOldToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var body = await withOldToken.ReadJsonAsync();
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("Sessão revogada. Entre novamente.");

        var newToken = await factory.LoginStaffAsync(slug, email);
        (await factory.ForStore(slug, newToken).GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Tampered_token_is_rejected()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var token = await factory.LoginStaffAsync(slug, email);
        var tampered = token[..^4] + (token.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");

        var response = await factory.ForStore(slug, tampered).GetAsync("/api/v1/auth/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── Clientes da vitrine ──────────────────────────────────────────────

    [Fact]
    public async Task Customer_registers_logs_in_and_cannot_use_admin_routes()
    {
        var (slug, _) = await factory.SignupStoreAsync();
        var store = factory.ForStore(slug);

        var register = await store.PostAsJsonAsync("/api/v1/auth/register", new
        {
            name = "Ana Cliente",
            email = "ana@cliente.com.br",
            password = "senha1234",
            phone = "11999990000",
        });
        register.StatusCode.ShouldBe(HttpStatusCode.Created);

        var login = await store.PostAsJsonAsync("/api/v1/auth/login", new { email = "ana@cliente.com.br", password = "senha1234" });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await login.ReadJsonAsync();
        body.Data().GetProperty("user").GetProperty("role").GetString().ShouldBe("customer");
        var token = body.Data().GetProperty("accessToken").GetString()!;
        using (var payload = TestApi.JwtPayload(token))
        {
            var claims = payload.RootElement;
            (claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64()).ShouldBe(7 * 24 * 3600);
            claims.TryGetProperty("sessionVersion", out _).ShouldBeFalse();
        }

        var adminRoute = await factory.ForStore(slug, token).PostAsync("/api/v1/auth/admin/revoke-sessions", null);
        adminRoute.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var forbidden = await adminRoute.ReadJsonAsync();
        forbidden.ErrorBody().GetProperty("code").GetString().ShouldBe("FORBIDDEN");
    }

    [Fact]
    public async Task Same_email_is_one_account_per_store()
    {
        var (storeA, _) = await factory.SignupStoreAsync();
        var (storeB, _) = await factory.SignupStoreAsync();
        var customer = new { name = "Ana", email = "ana@duas-lojas.com.br", password = "senha1234" };

        (await factory.ForStore(storeA).PostAsJsonAsync("/api/v1/auth/register", customer)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await factory.ForStore(storeB).PostAsJsonAsync("/api/v1/auth/register", customer)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var duplicate = await factory.ForStore(storeA).PostAsJsonAsync("/api/v1/auth/register", customer);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = await duplicate.ReadJsonAsync();
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("Já existe uma conta com este e-mail.");
    }

    [Fact]
    public async Task Unknown_host_has_no_store_and_cannot_register_customers()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("x-tenant-host", "loja-que-nao-existe.localhost");

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { name = "Ana", email = "ana@x.com.br", password = "senha1234" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var body = await response.ReadJsonAsync();
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("Loja não resolvida.");
    }

    // ── Loja suspensa ────────────────────────────────────────────────────

    [Fact]
    public async Task Suspended_store_blocks_the_storefront_but_allows_staff_login()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        await SuspendAsync(slug);

        var storefront = await factory.ForStore(slug).PostAsJsonAsync("/api/v1/auth/login", new { email = "a@b.com.br", password = "senha1234" });
        storefront.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var body = await storefront.ReadJsonAsync();
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("Esta loja está temporariamente suspensa.");

        var staffLogin = await factory.ForStore(slug).PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = TestApi.StrongPassword });
        staffLogin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ── Recurso por plano ────────────────────────────────────────────────

    [Fact]
    public async Task Plan_feature_gate_uses_the_effective_plan()
    {
        var (slug, email) = await factory.SignupStoreAsync(); // teste grátis = Pro
        var token = await factory.LoginStaffAsync(slug, email);
        var client = factory.ForStore(slug, token);

        (await client.GetAsync("/api/v1/_probe/plan/pdv")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var fiscal = await client.GetAsync("/api/v1/_probe/plan/fiscal"); // só no Complete
        fiscal.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var body = await fiscal.ReadJsonAsync();
        body.ErrorBody().GetProperty("code").GetString().ShouldBe("PLAN_UPGRADE_REQUIRED");
    }

    // ── Rate limiting ────────────────────────────────────────────────────

    [Fact]
    public async Task Login_is_rate_limited_per_store_and_ip()
    {
        var (slug, _) = await factory.SignupStoreAsync();
        using var limited = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{RateLimitingSettings.SectionName}:{nameof(RateLimitingSettings.AuthPermitLimit)}"] = "2",
            })));
        var client = limited.CreateClient();
        client.DefaultRequestHeaders.Add("x-tenant-host", $"{slug}.localhost");
        var attempt = new { email = "x@y.com.br", password = "senha-errada" };

        (await client.PostAsJsonAsync("/api/v1/auth/login", attempt)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/auth/login", attempt)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var third = await client.PostAsJsonAsync("/api/v1/auth/login", attempt);

        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        using var body = await third.ReadJsonAsync();
        body.ErrorBody().GetProperty("code").GetString().ShouldBe("RATE_LIMITED");
    }

    private async Task SuspendAsync(string slug)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var tenant = await db.Tenants.SingleAsync(t => t.Slug == TenantSlug.FromPersistence(slug));
        tenant.Suspend();
        await db.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<ITenantResolver>().Invalidate();
    }
}
