using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Application.Abstractions;
using Floruna.Application.Payments;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Outbox;
using Floruna.Infrastructure.Persistence;
using Floruna.SharedKernel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Fase 5: PIX direto, Mercado Pago (adaptador real + HTTP falso), webhook verificado, confirmação manual.</summary>
[Collection(ApiCollection.Name)]
public sealed class PaymentsTests(FlorunaApiFactory factory)
{
    private const string MpToken = "APP_USR-token-secreto-da-loja";

    // ── Segredos ─────────────────────────────────────────────────────────

    [Fact]
    public void Secrets_round_trip_only_in_their_own_context()
    {
        var protector = factory.Services.GetRequiredService<ISecretProtector>();
        var storeA = SecretContexts.MercadoPagoToken(TenantId.New());
        var storeB = SecretContexts.MercadoPagoToken(TenantId.New());

        var cipher = protector.Protect(MpToken, storeA);

        cipher.ShouldNotContain(MpToken);
        protector.Protect(MpToken, storeA).ShouldNotBe(cipher); // nonce aleatório: mesmo texto, cifras diferentes
        protector.Unprotect(cipher, storeA).ShouldBe(MpToken);
        protector.Unprotect(cipher, storeB).ShouldBeNull();     // copiar para outra loja não funciona
        protector.Unprotect(cipher[..^4] + "AAAA", storeA).ShouldBeNull(); // adulterado
    }

    [Fact]
    public async Task Gateway_token_is_write_only_and_encrypted_at_rest()
    {
        var store = await StoreAsync(factory);

        using var body = await (await store.Admin.PutAsJsonAsync("/api/v1/settings/payments",
            new { paymentGateway = "mercadopago", mercadoPagoAccessToken = MpToken })).ReadJsonAsync();
        body.Data().GetProperty("mercadoPagoConfigured").GetBoolean().ShouldBeTrue();
        body.RootElement.GetRawText().ShouldNotContain(MpToken);

        using var scope = factory.Services.CreateScope();
        var raw = await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Database
            .SqlQuery<string>($"SELECT mercado_pago_token_ciphertext AS \"Value\" FROM payment_settings").ToListAsync();
        raw.ShouldAllBe(c => c == null || !c.Contains(MpToken));
    }

    [Fact]
    public async Task Free_plan_cannot_enable_the_gateway()
    {
        var store = await FreeStoreAsync();

        var response = await store.Admin.PutAsJsonAsync("/api/v1/settings/payments", new { paymentGateway = "mercadopago" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var body = await response.ReadJsonAsync();
        body.ErrorBody().GetProperty("code").GetString().ShouldBe("PLAN_UPGRADE_REQUIRED");
    }

    // ── PIX direto e confirmação manual ──────────────────────────────────

    [Fact]
    public async Task Offline_pix_is_generated_at_checkout_and_manual_confirmation_sells_the_stock()
    {
        var store = await StoreAsync(factory);
        await store.Admin.PutAsJsonAsync("/api/v1/settings/payments", new { pixKey = "pix@loja.com.br", pixReceiverName = "Flora e Lis", pixCity = "Sao Paulo" });
        var product = await store.ProductAsync(stock: 5);

        var order = await store.CheckoutAsync(product, quantity: 2);
        order.GetProperty("paymentRail").GetString().ShouldBe("pix_offline");
        order.GetProperty("paymentInitializationStatus").GetString().ShouldBe("ready");
        var pix = order.GetProperty("pixCode").GetString()!;
        pix.ShouldStartWith("000201010212");
        pix.ShouldContain("0115pix@loja.com.br");
        pix.ShouldContain("5406200.00"); // 2 × R$ 100,00

        var intentId = await IntentIdAsync(store, order);
        var wrong = await store.Admin.PostAsJsonAsync($"/api/v1/payments/intents/{intentId}/confirm-manual", Manual(19_999));
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var confirm = await store.Admin.PostAsJsonAsync($"/api/v1/payments/intents/{intentId}/confirm-manual", Manual(20_000));
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());
        using (var body = await confirm.ReadJsonAsync())
        {
            body.Data().GetProperty("paymentIntent").GetProperty("status").GetString().ShouldBe("paid");
            body.Data().GetProperty("transactions")[0].GetProperty("evidence").GetProperty("reference").GetString().ShouldBe("E123456789");
        }

        (await store.Admin.PostAsJsonAsync($"/api/v1/payments/intents/{intentId}/confirm-manual", Manual(20_000))).StatusCode.ShouldBe(HttpStatusCode.OK); // replay

        var updated = await store.OrderAsync(order);
        updated.GetProperty("status").GetString().ShouldBe("confirmado");
        updated.GetProperty("paymentStatus").GetString().ShouldBe("approved");
        (await store.AvailabilityAsync(product)).GetProperty("onHand").GetInt32().ShouldBe(3); // vendeu 2, só uma vez
    }

    [Fact]
    public async Task Without_any_payment_setup_the_order_exists_but_initialization_failed()
    {
        var store = await StoreAsync(factory);
        var order = await store.CheckoutAsync(await store.ProductAsync(stock: 1));

        order.GetProperty("paymentInitializationStatus").GetString().ShouldBe("failed");
        order.GetProperty("status").GetString().ShouldBe("pendente");
    }

    // ── Mercado Pago ─────────────────────────────────────────────────────

    [Fact]
    public async Task Gateway_pix_is_confirmed_only_after_verifying_the_payment_at_the_provider()
    {
        var mp = new FakeMercadoPago();
        using var api = factory.WithMercadoPago(mp);
        var store = await StoreAsync(api);
        await store.Admin.PutAsJsonAsync("/api/v1/settings/payments", new { paymentGateway = "mercadopago", mercadoPagoAccessToken = MpToken });
        var product = await store.ProductAsync(stock: 3);

        var order = await store.CheckoutAsync(product);
        order.GetProperty("paymentRail").GetString().ShouldBe("mercadopago");
        order.GetProperty("pixCode").GetString()!.ShouldStartWith("00020101MP");
        mp.LastAuthorization.ShouldBe($"Bearer {MpToken}"); // o token decifrado foi usado na chamada
        mp.LastNotificationUrl.ShouldBe($"https://api.floruna.test/api/v1/payments/webhook?tenant={store.Slug}");
        var providerId = await ProviderIdAsync(store, order);

        // Webhook FORJADO dizendo "aprovado": o provedor ainda diz "pending" → nada muda.
        await WebhookAsync(api, store.Slug, providerId);
        await DrainAsync(api);
        (await store.OrderAsync(order)).GetProperty("status").GetString().ShouldBe("pendente");

        // O pagamento é aprovado DE VERDADE no provedor; o próximo aviso confirma.
        mp.SetStatus(providerId, "approved");
        await WebhookAsync(api, store.Slug, providerId);
        await WebhookAsync(api, store.Slug, providerId); // aviso duplicado
        await DrainAsync(api);

        var confirmed = await store.OrderAsync(order);
        confirmed.GetProperty("status").GetString().ShouldBe("confirmado");
        confirmed.GetProperty("paymentStatus").GetString().ShouldBe("approved");
        (await store.AvailabilityAsync(product)).GetProperty("onHand").GetInt32().ShouldBe(2); // baixou uma vez só
    }

    [Fact]
    public async Task Payment_after_abandonment_is_kept_and_flagged_for_reconciliation()
    {
        var mp = new FakeMercadoPago();
        using var api = factory.WithMercadoPago(mp);
        var store = await StoreAsync(api);
        await store.Admin.PutAsJsonAsync("/api/v1/settings/payments", new { paymentGateway = "mercadopago", mercadoPagoAccessToken = MpToken });
        var order = await store.CheckoutAsync(await store.ProductAsync(stock: 3));
        var providerId = await ProviderIdAsync(store, order);
        await store.Admin.PatchAsJsonAsync($"/api/v1/orders/{order.GetProperty("id").GetString()}/status", new { status = "cancelado", reason = "abandonado" });

        mp.SetStatus(providerId, "approved");
        await WebhookAsync(api, store.Slug, providerId);
        await DrainAsync(api);

        var updated = await store.OrderAsync(order);
        updated.GetProperty("status").GetString().ShouldBe("cancelado");
        updated.GetProperty("paymentStatus").GetString().ShouldBe("approved"); // o dinheiro entrou: não some
        using var intent = await (await store.Admin.GetAsync($"/api/v1/payments/intents?orderId={order.GetProperty("id").GetString()}")).ReadJsonAsync();
        intent.Data().GetProperty("paymentIntent").GetProperty("status").GetString().ShouldBe("paid");
        intent.Data().GetProperty("paymentIntent").GetProperty("reconciliationIssue").GetString()!.ShouldStartWith("order_not_confirmed");
    }

    [Theory]
    [InlineData(true, "pix_offline", "ready")]
    [InlineData(false, null, "failed")]
    public async Task Gateway_outage_falls_back_to_direct_pix_only_when_enabled(bool fallback, string? rail, string status)
    {
        var mp = new FakeMercadoPago { Down = true };
        using var api = factory.WithMercadoPago(mp);
        var store = await StoreAsync(api);
        await store.Admin.PutAsJsonAsync("/api/v1/settings/payments", new
        {
            paymentGateway = "mercadopago",
            mercadoPagoAccessToken = MpToken,
            mercadoPagoOfflineFallbackEnabled = fallback,
            pixKey = "pix@loja.com.br",
            pixReceiverName = "Loja",
            pixCity = "Recife",
        });

        var order = await store.CheckoutAsync(await store.ProductAsync(stock: 2));

        order.GetProperty("paymentInitializationStatus").GetString().ShouldBe(status);
        (order.GetProperty("paymentRail").ValueKind == JsonValueKind.Null ? null : order.GetProperty("paymentRail").GetString()).ShouldBe(rail);
        mp.Calls.ShouldBeGreaterThan(1); // a resiliência retentou antes de desistir
    }

    [Fact]
    public async Task Webhook_always_answers_ok_and_ignores_garbage()
    {
        var store = await StoreAsync(factory);
        var client = factory.CreateClient();

        (await client.PostAsJsonAsync($"/api/v1/payments/webhook?tenant={store.Slug}", new { type = "merchant_order", data = new { id = 1 } })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/v1/payments/webhook?tenant={store.Slug}", new { type = "payment", data = new { id = "../../etc/passwd" } })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/v1/payments/webhook?tenant=loja-que-nao-existe", new { type = "payment", data = new { id = 123 } })).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        (await db.PaymentNotifications.IgnoreQueryFilters().CountAsync(n => n.ProviderPaymentId == "../../etc/passwd")).ShouldBe(0);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private static object Manual(long amount) => new
    {
        amount,
        externalId = "E123456789",
        evidence = new { type = "pix_receipt", reference = "E123456789", note = "conferido no extrato" },
        idempotencyKey = "manual-1",
    };

    private static Task WebhookAsync(WebApplicationFactory<Program> api, string slug, string paymentId) =>
        api.CreateClient().PostAsJsonAsync($"/api/v1/payments/webhook?tenant={slug}",
            new { type = "payment", action = "payment.updated", data = new { id = long.Parse(paymentId, System.Globalization.CultureInfo.InvariantCulture) } });

    private static async Task DrainAsync(WebApplicationFactory<Program> api)
    {
        var processor = api.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 100 && await processor.ProcessOnceAsync(CancellationToken.None) > 0; i++)
        {
        }
    }

    private static async Task<string> IntentIdAsync(Store store, JsonElement order)
    {
        using var body = await (await store.Admin.GetAsync($"/api/v1/payments/intents?orderId={order.GetProperty("id").GetString()}")).ReadJsonAsync();
        return body.Data().GetProperty("paymentIntent").GetProperty("id").GetString()!;
    }

    private static async Task<string> ProviderIdAsync(Store store, JsonElement order)
    {
        using var body = await (await store.Admin.GetAsync($"/api/v1/payments/intents?orderId={order.GetProperty("id").GetString()}")).ReadJsonAsync();
        return body.Data().GetProperty("paymentIntent").GetProperty("providerPaymentId").GetString()!;
    }

    private async Task<Store> StoreAsync(WebApplicationFactory<Program> api)
    {
        var (slug, email) = await factory.SignupStoreAsync();
        return new Store(slug, api.ForStore(slug, await factory.LoginStaffAsync(slug, email)), api.ForStore(slug));
    }

    private async Task<Store> FreeStoreAsync()
    {
        var slug = TestApi.NewSlug();
        var email = $"dona@{slug}.com.br";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
            var tenant = Tenant.Create("Loja Grátis", slug, DateTimeOffset.UtcNow).Value;
            db.Tenants.Add(tenant);
            using (scope.ServiceProvider.GetRequiredService<TenantContext>().Enter(new ResolvedTenant(tenant.Id, slug, tenant.Status, TenantPlan.Free)))
            {
                var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(TestApi.StrongPassword);
                db.StaffUsers.Add(StaffUser.Create(tenant.Id, "Dona", Email.Create(email).Value, hash, Roles.Master, DateTimeOffset.UtcNow).Value);
                await db.SaveChangesAsync();
            }
        }

        return new Store(slug, factory.ForStore(slug, await factory.LoginStaffAsync(slug, email)), factory.ForStore(slug));
    }

    private sealed record Store(string Slug, HttpClient Admin, HttpClient Shopper)
    {
        public async Task<string> ProductAsync(int stock)
        {
            var response = await Admin.PostAsJsonAsync("/api/v1/products", new
            {
                name = $"Produto {Guid.NewGuid():N}"[..20],
                shortDescription = "Uma descrição curta",
                description = "Uma descrição longa o suficiente.",
                category = "buques",
                price = 10_000,
                images = new[] { "https://cdn.test/1.webp" },
                stock,
                isActive = true,
                isFeatured = false,
            });
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("product").GetProperty("id").GetString()!;
        }

        public async Task<JsonElement> CheckoutAsync(string productId, int quantity = 1)
        {
            var response = await Shopper.PostAsJsonAsync("/api/v1/orders", new
            {
                customerName = "Ana Compradora",
                customerPhone = "(11) 99999-0000",
                customerEmail = "ana@compradora.com.br",
                items = new[] { new { productId, quantity } },
                deliveryMethod = "retirada",
            });
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("order").Clone();
        }

        public async Task<JsonElement> OrderAsync(JsonElement order)
        {
            using var body = await (await Admin.GetAsync($"/api/v1/orders/{order.GetProperty("id").GetString()}")).ReadJsonAsync();
            return body.Data().GetProperty("order").Clone();
        }

        public async Task<JsonElement> AvailabilityAsync(string productId)
        {
            using var body = await (await Admin.GetAsync("/api/v1/products/manage?limit=100")).ReadJsonAsync();
            return body.Data().GetProperty("products").EnumerateArray()
                .Single(p => p.GetProperty("id").GetString() == productId).GetProperty("availability").Clone();
        }
    }
}
