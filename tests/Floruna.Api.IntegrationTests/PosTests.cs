using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Application.Abstractions;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Persistence;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>PDV e caixa: venda de balcão, pagamento dividido, conferência e corridas com o fechamento.</summary>
[Collection(ApiCollection.Name)]
public sealed class PosTests(FlorunaApiFactory factory)
{
    [Fact]
    public async Task A_full_shift_open_sell_withdraw_and_close_with_the_count()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 5);
        (await store.Cashier.PostAsJsonAsync("/api/v1/cash-sessions/open", new { openingFloat = 10_000 })).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Buquê de R$ 100: R$ 80 em dinheiro + R$ 20 no PIX.
        var sale = await store.SellAsync(product, quantity: 1, new { method = "dinheiro", amount = 8_000 }, new { method = "pix", amount = 2_000 });
        sale.StatusCode.ShouldBe(HttpStatusCode.Created, await sale.Content.ReadAsStringAsync());
        using (var body = await sale.ReadJsonAsync())
        {
            var order = body.Data().GetProperty("order");
            order.GetProperty("origin").GetString().ShouldBe("pdv");
            order.GetProperty("status").GetString().ShouldBe("entregue");
            order.GetProperty("paymentStatus").GetString().ShouldBe("approved");
            order.GetProperty("paymentMethod").GetString().ShouldBe("multiplo");
        }

        var current = await store.CurrentAsync();
        current.GetProperty("cashSales").GetInt64().ShouldBe(8_000);
        current.GetProperty("expectedCash").GetInt64().ShouldBe(18_000); // só o dinheiro entra na gaveta

        (await store.Cashier.PostAsJsonAsync("/api/v1/cash-sessions/movement", new { type = "sangria", amount = 20_000 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await store.Cashier.PostAsJsonAsync("/api/v1/cash-sessions/movement", new { type = "sangria", amount = 5_000, reason = "Cofre" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var close = await store.Cashier.PostAsJsonAsync("/api/v1/cash-sessions/close", new { countedCash = 12_500 });
        close.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var closed = await close.ReadJsonAsync())
        {
            closed.Data().GetProperty("status").GetString().ShouldBe("closed");
            closed.Data().GetProperty("expectedCash").GetInt64().ShouldBe(13_000);
            closed.Data().GetProperty("difference").GetInt64().ShouldBe(-500); // quebra de caixa registrada
        }

        using (var none = await (await store.Cashier.GetAsync("/api/v1/cash-sessions/current")).ReadJsonAsync())
        {
            none.Data().GetProperty("session").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        (await store.OnHandAsync(product)).ShouldBe(4);
    }

    [Fact]
    public async Task Payments_must_match_and_missing_stock_leaves_nothing_behind()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 1);

        (await store.SellAsync(product, 1, new { method = "dinheiro", amount = 9_999 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await store.SellAsync(product, 2, new { method = "dinheiro", amount = 20_000 })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await store.OnHandAsync(product)).ShouldBe(1);
        (await CountAsync<long>($"SELECT count(*) FROM orders WHERE origin = 'pdv' AND tenant_id = (SELECT id FROM tenants WHERE slug = '{store.Slug}')")).ShouldBe(0);
    }

    [Fact]
    public async Task Double_click_with_the_same_key_sells_once()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 5);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            store.SellAsync(product, 1, "venda-123", new { method = "pix", amount = 10_000 })));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created);
        (await store.OnHandAsync(product)).ShouldBe(4);
    }

    [Fact]
    public async Task Only_one_register_can_be_open_per_operator()
    {
        var store = await StoreAsync();

        var opens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            store.Cashier.PostAsJsonAsync("/api/v1/cash-sessions/open", new { openingFloat = 5_000 })));

        opens.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        opens.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(4);
    }

    [Fact]
    public async Task Sales_racing_the_close_are_either_in_the_count_or_outside_the_register()
    {
        var store = await StoreAsync();
        var product = await store.ProductAsync(stock: 100);
        await store.Cashier.PostAsJsonAsync("/api/v1/cash-sessions/open", new { openingFloat = 10_000 });
        var sessionId = (await store.CurrentAsync()).GetProperty("id").GetString();

        var sales = Enumerable.Range(0, 12).Select(_ => store.SellAsync(product, 1, new { method = "dinheiro", amount = 10_000 })).ToList();
        var close = store.Cashier.PostAsJsonAsync("/api/v1/cash-sessions/close", new { countedCash = 0 });
        await Task.WhenAll([.. sales, close]);

        using var closed = await (await close).ReadJsonAsync();
        var expectedAtClose = closed.Data().GetProperty("expectedCash").GetInt64();
        var cashInSession = await CountAsync<long>($"SELECT coalesce(sum(cash_received_cents), 0) FROM orders WHERE cash_session_id = '{sessionId}'");

        // Toda venda carimbada com o caixa entrou na conferência; nenhuma entrou depois do fechamento.
        expectedAtClose.ShouldBe(10_000 + cashInSession);
        sales.ShouldAllBe(s => s.Result.StatusCode == HttpStatusCode.Created);
    }

    [Fact]
    public async Task Pdv_requires_the_plan_and_a_counter_role()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        await SqlAsync($"UPDATE tenants SET trial_ends_at = now() - interval '1 day' WHERE slug = '{slug}'");
        var freeAdmin = factory.ForStore(slug, await factory.LoginStaffAsync(slug, email));
        (await freeAdmin.PostAsJsonAsync("/api/v1/cash-sessions/open", new { openingFloat = 0 })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var store = await StoreAsync();
        await store.Shopper.PostAsJsonAsync("/api/v1/auth/register", new { name = "Ana", email = "ana@pdv.com.br", password = "senha1234", phone = "11999990000" });
        using var login = await (await store.Shopper.PostAsJsonAsync("/api/v1/auth/login", new { email = "ana@pdv.com.br", password = "senha1234" })).ReadJsonAsync();
        var customer = factory.ForStore(store.Slug, login.Data().GetProperty("accessToken").GetString());
        (await customer.PostAsJsonAsync("/api/v1/cash-sessions/open", new { openingFloat = 0 })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private async Task SqlAsync(string sql)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<T> CountAsync<T>(string sql)
    {
        using var scope = factory.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(await command.ExecuteScalarAsync(), typeof(T), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    /// <summary>Loja com dona (cadastra produto) e uma operadora de caixa (papel <c>caixa</c>).</summary>
    private async Task<Store> StoreAsync()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        var cashierEmail = $"caixa@{slug}.com.br";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
            var tenant = await db.Tenants.SingleAsync(t => t.Slug == TenantSlug.FromPersistence(slug));
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(new ResolvedTenant(tenant.Id, slug, tenant.Status, TenantPlan.Pro));
            var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(TestApi.StrongPassword);
            db.StaffUsers.Add(StaffUser.Create(tenant.Id, "Carla Caixa", Email.Create(cashierEmail).Value, hash, Roles.Cashier, DateTimeOffset.UtcNow).Value);
            await db.SaveChangesAsync();
        }

        return new Store(slug, factory.ForStore(slug, await factory.LoginStaffAsync(slug, email)),
            factory.ForStore(slug, await factory.LoginStaffAsync(slug, cashierEmail)), factory.ForStore(slug));
    }

    private sealed record Store(string Slug, HttpClient Admin, HttpClient Cashier, HttpClient Shopper)
    {
        public Task<HttpResponseMessage> SellAsync(string productId, int quantity, params object[] payments) =>
            SellAsync(productId, quantity, null, payments);

        public Task<HttpResponseMessage> SellAsync(string productId, int quantity, string? idempotencyKey, params object[] payments) =>
            Cashier.PostAsJsonAsync("/api/v1/orders/pos", new { items = new[] { new { productId, quantity } }, payments, idempotencyKey });

        public async Task<JsonElement> CurrentAsync()
        {
            using var body = await (await Cashier.GetAsync("/api/v1/cash-sessions/current")).ReadJsonAsync();
            return body.Data().Clone();
        }

        public async Task<int> OnHandAsync(string productId)
        {
            using var body = await (await Admin.GetAsync("/api/v1/products/manage?limit=100")).ReadJsonAsync();
            return body.Data().GetProperty("products").EnumerateArray().Single(p => p.GetProperty("id").GetString() == productId)
                .GetProperty("availability").GetProperty("onHand").GetInt32();
        }

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
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("product").GetProperty("id").GetString()!;
        }
    }
}
