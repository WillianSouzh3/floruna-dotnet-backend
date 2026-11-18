using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Infrastructure.Ordering;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Fase 4: checkout, cupom, entrega e máquina de estados contra PostgreSQL real.</summary>
[Collection(ApiCollection.Name)]
public sealed class CheckoutTests(FlorunaApiFactory factory)
{
    private static readonly string DeliveryDay = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // ── Caminho feliz ────────────────────────────────────────────────────

    [Fact]
    public async Task Checkout_prices_on_the_server_reserves_stock_and_returns_a_public_token()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 5, price: 10_000, variants: [new { label = "G", price = 15_000 }]);
        await store.Admin.PostAsJsonAsync("/api/v1/delivery-zones", new { name = "Centro", zipStart = "01000-000", zipEnd = "01999-999", fee = 1_500 });
        await store.Admin.PostAsJsonAsync("/api/v1/coupons", new { code = "flor10", type = "percent", value = 10 });

        var response = await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(product, quantity: 2, variant: "G", coupon: "FLOR10"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var body = await response.ReadJsonAsync();
        var order = body.Data().GetProperty("order");
        order.GetProperty("subtotal").GetInt64().ShouldBe(30_000);    // preço da variação, do catálogo
        order.GetProperty("discount").GetInt64().ShouldBe(3_000);     // 10%
        order.GetProperty("deliveryFee").GetInt64().ShouldBe(1_500);  // zona do CEP
        order.GetProperty("total").GetInt64().ShouldBe(28_500);
        order.GetProperty("status").GetString().ShouldBe("pendente");
        order.GetProperty("items")[0].TryGetProperty("unitCost", out _).ShouldBeFalse(); // custo nunca vai ao comprador
        var token = order.GetProperty("accessToken").GetString()!;
        var id = order.GetProperty("id").GetString();

        (await store.AvailabilityAsync(product)).GetProperty("reserved").GetInt32().ShouldBe(2);

        (await factory.ForStore(store.Slug).GetAsync($"/api/v1/orders/{id}?token={token}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.ForStore(store.Slug).GetAsync($"/api/v1/orders/{id}?token=errado")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await factory.ForStore(store.Slug).GetAsync($"/api/v1/orders/{id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_order_even_under_concurrency()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 10);
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            factory.ForStore(store.Slug).PostAsJsonAsync("/api/v1/orders", Checkout(product, idempotencyKey: key))));

        var ids = new HashSet<string>();
        foreach (var response in responses)
        {
            response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            ids.Add(body.Data().GetProperty("order").GetProperty("id").GetString()!);
        }

        ids.Count.ShouldBe(1);
        (await store.AvailabilityAsync(product)).GetProperty("reserved").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_checkouts_never_sell_more_than_the_stock()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 3);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            factory.ForStore(store.Slug).PostAsJsonAsync("/api/v1/orders", Checkout(product))));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(3);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(7);
        (await store.AvailabilityAsync(product)).GetProperty("available").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task The_last_use_of_a_coupon_goes_to_only_one_buyer()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 10);
        await store.Admin.PostAsJsonAsync("/api/v1/coupons", new { code = "ULTIMO", type = "fixed", value = 1_000, maxUses = 1 });

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            factory.ForStore(store.Slug).PostAsJsonAsync("/api/v1/orders", Checkout(product, coupon: "ULTIMO"))));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            using var body = await refused.ReadJsonAsync();
            body.ErrorBody().GetProperty("code").GetString().ShouldBe("COUPON_EXHAUSTED");
        }
    }

    // ── Entrega ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Delivery_rules_zone_capacity_and_blackout_are_enforced_at_checkout()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 10);
        await store.Admin.PostAsJsonAsync("/api/v1/delivery-zones", new { name = "Centro", zipStart = "01000-000", zipEnd = "01999-999", fee = 1_000 });
        await store.Admin.PostAsJsonAsync("/api/v1/delivery-capacity", new { kind = "date", date = DeliveryDay, window = "tarde", capacity = 1 });

        var outside = await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(product, zipCode: "99999-000"));
        outside.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(product))).StatusCode.ShouldBe(HttpStatusCode.Created);
        var full = await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(product));
        full.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using (var body = await full.ReadJsonAsync())
        {
            body.ErrorBody().GetProperty("message").GetString().ShouldBe("Esta janela de entrega está lotada. Escolha outro horário.");
        }

        using var quote = await (await store.Shopper.PostAsJsonAsync("/api/v1/delivery-promises/quote",
            new { method = "entrega", zipCode = "01310-100", date = DeliveryDay, window = "tarde" })).ReadJsonAsync();
        quote.Data().GetProperty("promise").GetProperty("available").GetBoolean().ShouldBeFalse();

        await store.Admin.PostAsJsonAsync("/api/v1/delivery-capacity", new { kind = "blackout", date = DeliveryDay });
        var closed = await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(product, window: "manha"));
        using var closedBody = await closed.ReadJsonAsync();
        closedBody.ErrorBody().GetProperty("message").GetString().ShouldBe("A loja não entrega nesta data. Escolha outra data de entrega.");
    }

    [Fact]
    public async Task Past_window_is_refused()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 5);
        var yesterday = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var response = await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(product, date: yesterday));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ── Status ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirming_turns_the_reservation_into_a_sale_in_the_ledger()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 5);
        var orderId = await store.PlaceAsync(product, quantity: 2);

        var confirm = await store.Admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status",
            new { status = "confirmado", expectedState = "pendente", idempotencyKey = "c1" });
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);

        var availability = await store.AvailabilityAsync(product);
        availability.GetProperty("onHand").GetInt32().ShouldBe(3);
        availability.GetProperty("reserved").GetInt32().ShouldBe(0);

        using var movements = await (await store.Admin.GetAsync($"/api/v1/stock/{product}/movements")).ReadJsonAsync();
        var sale = movements.Data().GetProperty("movements")[0];
        sale.GetProperty("type").GetString().ShouldBe("venda");
        sale.GetProperty("orderId").GetString().ShouldBe(orderId);

        // Mesmo clique de novo: sucesso, sem baixar duas vezes.
        (await store.Admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status",
            new { status = "confirmado", expectedState = "pendente", idempotencyKey = "c1" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await store.AvailabilityAsync(product)).GetProperty("onHand").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Status_never_goes_back_and_stale_expected_state_is_rejected()
    {
        var store = await StoreAsync();
        var orderId = await store.PlaceAsync(await store.ProductAsync(stock: 5));
        await store.Admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status = "em_entrega" });

        (await store.Admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status = "em_preparo" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await store.Admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status",
            new { status = "entregue", expectedState = "confirmado", idempotencyKey = "x" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cancelling_a_pending_order_returns_stock_and_coupon_use()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 4);
        await store.Admin.PostAsJsonAsync("/api/v1/coupons", new { code = "VOLTA", type = "fixed", value = 500, maxUses = 1 });
        var orderId = await store.PlaceAsync(product, quantity: 4, coupon: "VOLTA");

        (await store.Admin.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status = "cancelado", reason = "cliente desistiu" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await store.AvailabilityAsync(product)).GetProperty("available").GetInt32().ShouldBe(4);
        using var coupons = await (await store.Admin.GetAsync("/api/v1/coupons")).ReadJsonAsync();
        coupons.Data().GetProperty("coupons")[0].GetProperty("usedCount").GetInt32().ShouldBe(0);
        // E o cupom volta a funcionar para outra pessoa.
        (await store.Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(product, coupon: "VOLTA"))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Abandoned_orders_are_cancelled_by_the_job_and_release_stock()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 2);
        var orderId = await store.PlaceAsync(product, quantity: 2);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE orders SET payment_deadline = now() - interval '1 minute' WHERE id = {Guid.Parse(orderId)}");
        }

        (await factory.Services.GetRequiredService<AbandonedOrdersJob>().RunOnceAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        using var order = await (await store.Admin.GetAsync($"/api/v1/orders/{orderId}")).ReadJsonAsync();
        order.Data().GetProperty("order").GetProperty("status").GetString().ShouldBe("cancelado");
        (await store.AvailabilityAsync(product)).GetProperty("available").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Customer_sees_only_own_orders()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 10);
        var register = await factory.ForStore(store.Slug).PostAsJsonAsync("/api/v1/auth/register",
            new { name = "Ana", email = "ana@cliente.com.br", password = "senha1234" });
        using var auth = await register.ReadJsonAsync();
        var customer = factory.ForStore(store.Slug, auth.Data().GetProperty("accessToken").GetString());

        var mine = await customer.PostAsJsonAsync("/api/v1/orders", Checkout(product));
        mine.StatusCode.ShouldBe(HttpStatusCode.Created);
        await store.PlaceAsync(product); // de outra pessoa, sem login

        using var list = await (await customer.GetAsync("/api/v1/orders/me")).ReadJsonAsync();
        list.Data().GetProperty("total").GetInt32().ShouldBe(1);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private static object Checkout(string productId, int quantity = 1, string? variant = null, string? coupon = null, string? idempotencyKey = null,
        string zipCode = "01310-100", string? date = null, string window = "tarde") => new
        {
            idempotencyKey,
            customerName = "Cliente Teste",
            customerPhone = "(11) 99999-0000",
            customerEmail = "cliente@teste.com.br",
            items = new[] { new { productId, quantity, variantLabel = variant } },
            deliveryMethod = "entrega",
            address = new { recipientName = "Mãe", street = "Rua das Flores", number = "10", neighborhood = "Centro", city = "São Paulo", state = "SP", zipCode },
            deliveryDate = date ?? DeliveryDay,
            deliveryTimeWindow = window,
            couponCode = coupon,
        };

    private async Task<Store> StoreAsync()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        return new Store(factory, slug, factory.ForStore(slug, await factory.LoginStaffAsync(slug, email)), factory.ForStore(slug));
    }

    private sealed record Store(FlorunaApiFactory Factory, string Slug, HttpClient Admin, HttpClient Shopper)
    {
        public async Task<string> ProductAsync(int stock, long price = 10_000, object[]? variants = null)
        {
            var response = await Admin.PostAsJsonAsync("/api/v1/products", new
            {
                name = $"Produto {Guid.NewGuid():N}"[..20],
                shortDescription = "Uma descrição curta",
                description = "Uma descrição longa o suficiente.",
                category = "buques",
                price,
                cost = 4_000,
                images = new[] { "https://cdn.test/1.webp" },
                stock,
                isActive = true,
                isFeatured = false,
                variants,
            });
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("product").GetProperty("id").GetString()!;
        }

        public async Task<string> PlaceAsync(string productId, int quantity = 1, string? coupon = null)
        {
            var response = await Shopper.PostAsJsonAsync("/api/v1/orders", Checkout(productId, quantity, coupon: coupon));
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("order").GetProperty("id").GetString()!;
        }

        public async Task<JsonElement> AvailabilityAsync(string productId)
        {
            using var body = await (await Admin.GetAsync("/api/v1/products/manage?limit=100")).ReadJsonAsync();
            return body.Data().GetProperty("products").EnumerateArray()
                .Single(p => p.GetProperty("id").GetString() == productId).GetProperty("availability").Clone();
        }
    }
}
