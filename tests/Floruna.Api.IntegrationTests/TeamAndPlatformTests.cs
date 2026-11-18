using System.Net;
using System.Net.Http.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Application.Abstractions;
using Floruna.Domain.Audit;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Persistence;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Equipe com teto de plano, /tenants/me, domínio próprio e suporte (superadmin).</summary>
[Collection(ApiCollection.Name)]
public sealed class TeamAndPlatformTests(FlorunaApiFactory factory)
{
    // ── Equipe ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_manages_the_team_within_the_plan_limit()
    {
        var (slug, email) = await factory.SignupStoreAsync(); // teste grátis = Pro = até 5 usuários
        var owner = factory.ForStore(slug, await factory.LoginStaffAsync(slug, email));

        for (var i = 1; i <= 4; i++)
        {
            (await owner.PostAsJsonAsync("/api/v1/users", Member($"pessoa{i}@loja.com.br", "caixa"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var sixth = await owner.PostAsJsonAsync("/api/v1/users", Member("sexta@loja.com.br", "gerente"));
        sixth.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (var body = await sixth.ReadJsonAsync())
        {
            body.ErrorBody().GetProperty("message").GetString().ShouldBe("Seu plano permite até 5 usuário(s). Faça upgrade para ampliar a equipe.");
        }

        using var list = await (await owner.GetAsync("/api/v1/users")).ReadJsonAsync();
        var users = list.Data().GetProperty("users").EnumerateArray().ToList();
        users.Count.ShouldBe(5);
        foreach (var user in users)
        {
            user.TryGetProperty("passwordHash", out _).ShouldBeFalse();
            user.TryGetProperty("sessionVersion", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Duplicate_member_email_is_a_conflict()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var owner = factory.ForStore(slug, await factory.LoginStaffAsync(slug, email));

        (await owner.PostAsJsonAsync("/api/v1/users", Member("igual@loja.com.br", "gerente"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        var duplicate = await owner.PostAsJsonAsync("/api/v1/users", Member("igual@loja.com.br", "caixa"));

        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Store_never_loses_its_last_owner()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var token = await factory.LoginStaffAsync(slug, email);
        var owner = factory.ForStore(slug, token);
        var myId = (await MeAsync(slug, token)).sub;

        var demoteSelf = await owner.PatchAsJsonAsync($"/api/v1/users/{myId}", new { role = "gerente" });
        demoteSelf.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var removeSelf = await owner.DeleteAsync($"/api/v1/users/{myId}");
        removeSelf.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var body = await removeSelf.ReadJsonAsync();
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("Você não pode remover a própria conta.");
    }

    [Fact]
    public async Task Role_change_and_removal_end_the_member_sessions()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var owner = factory.ForStore(slug, await factory.LoginStaffAsync(slug, email));
        using var created = await (await owner.PostAsJsonAsync("/api/v1/users", Member("caixa@loja.com.br", "caixa"))).ReadJsonAsync();
        var memberId = created.Data().GetProperty("id").GetString();
        var memberToken = await factory.LoginStaffAsync(slug, "caixa@loja.com.br");

        // Caixa não gerencia equipe.
        (await factory.ForStore(slug, memberToken).GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using (var promoted = await (await owner.PatchAsJsonAsync($"/api/v1/users/{memberId}", new { role = "gerente" })).ReadJsonAsync())
        {
            promoted.Data().GetProperty("role").GetString().ShouldBe("gerente");
        }

        (await factory.ForStore(slug, memberToken).GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var newToken = await factory.LoginStaffAsync(slug, "caixa@loja.com.br");
        (await owner.DeleteAsync($"/api/v1/users/{memberId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await factory.ForStore(slug, newToken).GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── /tenants/me ──────────────────────────────────────────────────────

    [Fact]
    public async Task Tenant_me_shows_effective_plan_limits_and_referral()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var owner = factory.ForStore(slug, await factory.LoginStaffAsync(slug, email));

        using var body = await (await owner.GetAsync("/api/v1/tenants/me")).ReadJsonAsync();
        var data = body.Data();

        data.GetProperty("slug").GetString().ShouldBe(slug);
        data.GetProperty("planLabel").GetString().ShouldBe("Profissional");
        data.GetProperty("limits").GetProperty("maxUsers").GetInt32().ShouldBe(5);
        data.GetProperty("limits").GetProperty("allowPdv").GetBoolean().ShouldBeTrue();
        data.GetProperty("limits").GetProperty("allowFiscal").GetBoolean().ShouldBeFalse();
        data.GetProperty("referral").GetProperty("code").GetString()!.ShouldStartWith("FLORA-");
    }

    // ── Domínio próprio ──────────────────────────────────────────────────

    [Fact]
    public async Task Custom_domain_routes_only_after_dns_proof()
    {
        var dns = new FakeDnsTxtResolver();
        using var api = factory.WithDns(dns);
        var (slug, email) = await factory.SignupStoreAsync();
        var owner = api.ForStore(slug, await factory.LoginStaffAsync(slug, email));
        var domain = $"www.{slug}.com.br";

        var requested = await owner.PostAsJsonAsync("/api/v1/tenants/me/domain", new { domain = $"https://{domain}/" });
        requested.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var instructions = await requested.ReadJsonAsync();
        var txt = instructions.Data().GetProperty("txt");
        txt.GetProperty("name").GetString().ShouldBe($"_flora-verify.{slug}.com.br");
        instructions.Data().GetProperty("cname").GetProperty("value").GetString().ShouldBe($"{slug}.localhost");
        instructions.Data().GetProperty("status").GetString().ShouldBe("pending");

        // Antes da prova de posse, o domínio NÃO leva à loja.
        (await RegisterOnHostAsync(api, domain)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var notPublished = await owner.PostAsync("/api/v1/tenants/me/domain/verify", null);
        notPublished.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        dns.Publish(txt.GetProperty("name").GetString()!, "v=spf1 -all", txt.GetProperty("value").GetString()!);
        using (var verified = await (await owner.PostAsync("/api/v1/tenants/me/domain/verify", null)).ReadJsonAsync())
        {
            verified.Data().GetProperty("status").GetString().ShouldBe("verified");
        }

        (await RegisterOnHostAsync(api, domain)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Outra loja não consegue reivindicar um domínio já verificado.
        var (other, otherEmail) = await factory.SignupStoreAsync();
        var otherOwner = api.ForStore(other, await factory.LoginStaffAsync(other, otherEmail));
        (await otherOwner.PostAsJsonAsync("/api/v1/tenants/me/domain", new { domain })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await owner.DeleteAsync("/api/v1/tenants/me/domain")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RegisterOnHostAsync(api, domain)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await AuditCountAsync(slug, AuditActions.DomainChanged)).ShouldBe(3);
    }

    // ── Suporte (superadmin) ─────────────────────────────────────────────

    [Fact]
    public async Task Superadmin_impersonates_a_store_with_a_short_audited_single_use_session()
    {
        var (platformSlug, superEmail) = await CreateSuperAdminAsync();
        var superToken = await factory.LoginStaffAsync(platformSlug, superEmail);
        var (targetSlug, ownerEmail, targetId) = await factory.SignupStoreWithIdAsync();

        // O superadmin navega por outras lojas.
        (await factory.ForStore(targetSlug, superToken).GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var grant = await factory.ForStore(platformSlug, superToken).PostAsync($"/api/v1/auth/admin/impersonate/{targetId}", null);
        grant.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var grantBody = await grant.ReadJsonAsync();
        var code = grantBody.Data().GetProperty("code").GetString()!;
        grantBody.Data().GetProperty("storeUrl").GetString().ShouldBe($"https://{targetSlug}.localhost");

        var exchange = await factory.ForStore(targetSlug).PostAsJsonAsync("/api/v1/auth/admin/impersonation/exchange", new { code });
        exchange.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var session = await exchange.ReadJsonAsync();
        session.Data().GetProperty("user").GetProperty("email").GetString().ShouldBe(ownerEmail);
        using (var claims = TestApi.JwtPayload(session.Data().GetProperty("accessToken").GetString()!))
        {
            claims.RootElement.GetProperty("role").GetString().ShouldBe("master");
            claims.RootElement.GetProperty("impersonatedBy").GetString().ShouldNotBeNullOrEmpty();
            (claims.RootElement.GetProperty("exp").GetInt64() - claims.RootElement.GetProperty("iat").GetInt64()).ShouldBe(15 * 60);
        }

        (await factory.ForStore(targetSlug).PostAsJsonAsync("/api/v1/auth/admin/impersonation/exchange", new { code }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AuditCountAsync(targetSlug, AuditActions.SupportImpersonate)).ShouldBe(1);
    }

    [Fact]
    public async Task Only_superadmin_can_impersonate()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var (_, _, otherId) = await factory.SignupStoreWithIdAsync();
        var owner = factory.ForStore(slug, await factory.LoginStaffAsync(slug, email));

        (await owner.PostAsync($"/api/v1/auth/admin/impersonate/{otherId}", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private static object Member(string email, string role) =>
        new { name = "Pessoa da Equipe", email, password = TestApi.StrongPassword, role };

    private async Task<(string sub, string role)> MeAsync(string slug, string token)
    {
        using var body = await (await factory.ForStore(slug, token).GetAsync("/api/v1/auth/me")).ReadJsonAsync();
        var user = body.Data().GetProperty("user");
        return (user.GetProperty("sub").GetString()!, user.GetProperty("role").GetString()!);
    }

    private static Task<HttpResponseMessage> RegisterOnHostAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> api, string host)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("x-tenant-host", host);
        return client.PostAsJsonAsync("/api/v1/auth/register", new { name = "Cliente", email = $"c{Guid.NewGuid():N}@x.com.br", password = "senha1234" });
    }

    private async Task<int> AuditCountAsync(string slug, string action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var tenant = await db.Tenants.SingleAsync(t => t.Slug == TenantSlug.FromPersistence(slug));
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(new ResolvedTenant(tenant.Id, slug, tenant.Status, TenantPlan.Free));
        return await db.AuditEntries.CountAsync(a => a.Action == action);
    }

    private async Task<(string Slug, string Email)> CreateSuperAdminAsync()
    {
        var (slug, _) = await factory.SignupStoreAsync();
        var email = $"suporte@{slug}.com.br";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var tenant = await db.Tenants.SingleAsync(t => t.Slug == TenantSlug.FromPersistence(slug));
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(new ResolvedTenant(tenant.Id, slug, tenant.Status, TenantPlan.Free));
        var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(TestApi.StrongPassword);
        db.StaffUsers.Add(StaffUser.Create(tenant.Id, "Suporte", Email.Create(email).Value, hash, Roles.SuperAdmin, DateTimeOffset.UtcNow).Value);
        await db.SaveChangesAsync();
        return (slug, email);
    }
}
