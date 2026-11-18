using System.Net;
using System.Net.Http.Json;
using Floruna.Api.IntegrationTests.Support;

namespace Floruna.Api.IntegrationTests;

/// <summary>Bloqueio progressivo, 2FA por e-mail, "esqueci minha senha" e troca de senha.</summary>
[Collection(ApiCollection.Name)]
public sealed class AccountSecurityTests(FlorunaApiFactory factory)
{
    // ── Bloqueio progressivo ─────────────────────────────────────────────

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var store = factory.ForStore(slug);

        for (var i = 0; i < 5; i++)
        {
            (await store.PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = "senha-errada-123" }))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var locked = await store.PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = TestApi.StrongPassword });

        locked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        int.Parse(locked.Headers.RetryAfter!.ToString(), System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(1, 60);
        using var body = await locked.ReadJsonAsync();
        body.ErrorBody().GetProperty("code").GetString().ShouldBe("RATE_LIMITED");
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("Muitas tentativas de autenticação. Tente novamente mais tarde.");
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_count()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var store = factory.ForStore(slug);

        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 4; i++)
            {
                await store.PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = "senha-errada-123" });
            }

            (await store.PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = TestApi.StrongPassword }))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Locking_one_store_account_does_not_affect_the_same_email_in_another_store()
    {
        var (storeA, email) = await factory.SignupStoreAsync();
        var storeB = TestApi.NewSlug();
        // mesma pessoa, mesmo e-mail, outra loja
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/tenants/signup", new
        {
            storeName = "Loja B",
            slug = storeB,
            adminName = "Dona",
            email,
            password = TestApi.StrongPassword,
        })).EnsureSuccessStatusCode();

        for (var i = 0; i < 5; i++)
        {
            await factory.ForStore(storeA).PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = "senha-errada-123" });
        }

        (await factory.ForStore(storeB).PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = TestApi.StrongPassword }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ── 2FA por e-mail ───────────────────────────────────────────────────

    [Fact]
    public async Task With_email_configured_staff_login_requires_the_code_sent_by_email()
    {
        var mail = new CapturingEmailTransport();
        using var api = factory.WithEmail(mail);
        var (slug, email) = await factory.SignupStoreAsync();
        var store = api.ForStore(slug);

        var login = await store.PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = TestApi.StrongPassword });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var pending = await login.ReadJsonAsync();
        pending.Data().GetProperty("requiresTwoFactor").GetBoolean().ShouldBeTrue();
        var pendingToken = pending.Data().GetProperty("pendingToken").GetString()!;

        // O token pendente não abre sessão.
        (await api.ForStore(slug, pendingToken).GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var code = CapturingEmailTransport.CodeIn(await mail.WaitForAsync(email));
        var wrongCode = code == "000000" ? "111111" : "000000";

        (await store.PostAsJsonAsync("/api/v1/auth/admin/verify-otp", new { pendingToken, code = wrongCode }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var verified = await store.PostAsJsonAsync("/api/v1/auth/admin/verify-otp", new { pendingToken, code });
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var session = await verified.ReadJsonAsync();
        var token = session.Data().GetProperty("accessToken").GetString()!;
        (await api.ForStore(slug, token).GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // O mesmo código não serve duas vezes.
        var replay = await store.PostAsJsonAsync("/api/v1/auth/admin/verify-otp", new { pendingToken, code });
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var replayBody = await replay.ReadJsonAsync();
        replayBody.ErrorBody().GetProperty("message").GetString().ShouldBe("Código incorreto ou expirado.");
    }

    // ── Esqueci minha senha ──────────────────────────────────────────────

    [Fact]
    public async Task Password_reset_link_works_once_and_revokes_open_sessions()
    {
        var mail = new CapturingEmailTransport();
        using var api = factory.WithEmail(mail);
        var (slug, email) = await factory.SignupStoreAsync();
        var oldSession = await factory.LoginStaffAsync(slug, email);

        var forgot = await api.ForStore(slug).PostAsJsonAsync("/api/v1/auth/admin/forgot-password", new { email });
        forgot.StatusCode.ShouldBe(HttpStatusCode.OK);
        var message = await mail.WaitForAsync(email);
        message.TextBody.ShouldContain($"http://{slug}.localhost:3000/admin/recuperar-senha?token=");
        var token = CapturingEmailTransport.ResetTokenIn(message);

        const string newPassword = "uma-senha-nova-bem-longa";
        (await api.ForStore(slug).PostAsJsonAsync("/api/v1/auth/admin/reset-password", new { token, password = newPassword }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var reused = await api.ForStore(slug).PostAsJsonAsync("/api/v1/auth/admin/reset-password", new { token, password = newPassword });
        reused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using (var body = await reused.ReadJsonAsync())
        {
            body.ErrorBody().GetProperty("message").GetString().ShouldBe("Link inválido ou expirado. Solicite um novo.");
        }

        (await factory.ForStore(slug, oldSession).GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.ForStore(slug).PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = TestApi.StrongPassword }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.ForStore(slug).PostAsJsonAsync("/api/v1/auth/admin/login", new { email, password = newPassword }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Forgot_password_answers_the_same_for_unknown_emails()
    {
        var mail = new CapturingEmailTransport();
        using var api = factory.WithEmail(mail);
        var (slug, _) = await factory.SignupStoreAsync();

        var response = await api.ForStore(slug).PostAsJsonAsync("/api/v1/auth/admin/forgot-password", new { email = "ninguem@lugar.com.br" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        body.Data().GetProperty("ok").GetBoolean().ShouldBeTrue();
        await Task.Delay(300);
        mail.AnySentTo("ninguem@lugar.com.br").ShouldBeFalse();
    }

    [Fact]
    public async Task A_reset_link_from_one_store_does_not_work_in_another()
    {
        var mail = new CapturingEmailTransport();
        using var api = factory.WithEmail(mail);
        var (storeA, emailA) = await factory.SignupStoreAsync();
        var (storeB, _) = await factory.SignupStoreAsync();

        await api.ForStore(storeA).PostAsJsonAsync("/api/v1/auth/admin/forgot-password", new { email = emailA });
        var token = CapturingEmailTransport.ResetTokenIn(await mail.WaitForAsync(emailA));

        (await api.ForStore(storeB).PostAsJsonAsync("/api/v1/auth/admin/reset-password", new { token, password = "uma-senha-nova-bem-longa" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── Troca da própria senha ───────────────────────────────────────────

    [Fact]
    public async Task Changing_own_password_requires_the_current_one_and_ends_the_session()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var token = await factory.LoginStaffAsync(slug, email);
        var me = factory.ForStore(slug, token);

        var wrong = await me.PatchAsJsonAsync("/api/v1/users/me/password", new { currentPassword = "nao-e-essa", newPassword = "outra-senha-bem-longa" });
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var body = await wrong.ReadJsonAsync())
        {
            body.ErrorBody().GetProperty("message").GetString().ShouldBe("Senha atual incorreta.");
        }

        (await me.PatchAsJsonAsync("/api/v1/users/me/password", new { currentPassword = TestApi.StrongPassword, newPassword = "outra-senha-bem-longa" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await me.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
