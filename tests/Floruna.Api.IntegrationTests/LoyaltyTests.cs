using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Infrastructure.Outbox;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Fidelidade: cashback creditado na entrega, resgatado no checkout (com sessão), devolvido no cancelamento.</summary>
[Collection(ApiCollection.Name)]
public sealed class LoyaltyTests(FlorunaApiFactory factory)
{
    private const string Buyer = "ana@fiel.com.br";

    [Fact]
    public async Task Program_is_off_by_default_and_checkout_is_unchanged()
    {
        var store = await StoreAsync();
        using var program = await (await store.Admin.GetAsync("/api/v1/loyalty/program")).ReadJsonAsync();
        program.Data().GetProperty("program").GetProperty("enabled").GetBoolean().ShouldBeFalse();
        program.Data().GetProperty("program").GetProperty("tiers").GetArrayLength().ShouldBe(5);

        var order = await store.CheckoutAsync(store.Shopper, await store.ProductAsync(5));

        order.GetProperty("tierDiscount").GetInt64().ShouldBe(0);
        order.GetProperty("cashbackUsed").GetInt64().ShouldBe(0);
        order.GetProperty("total").GetInt64().ShouldBe(10_000);
    }

    [Fact]
    public async Task Cashback_is_earned_on_delivery_redeemed_at_checkout_and_returned_on_cancel()
    {
        var store = await StoreAsync();
        await store.EnableAsync(cashbackPercent: 10, minRedemption: 100);
        var customer = await store.CustomerAsync(Buyer);
        var product = await store.ProductAsync(10);

        // 1ª compra entregue: 10% de 100,00 = 10,00 de cashback.
        var first = await store.CheckoutAsync(customer, product);
        await store.ChangeStatusAsync(first, "entregue");
        await DrainAsync();
        await DrainAsync(); // replay da outbox não credita de novo

        using (var wallet = await (await customer.GetAsync("/api/v1/loyalty/me")).ReadJsonAsync())
        {
            wallet.Data().GetProperty("account").GetProperty("cashbackBalance").GetInt64().ShouldBe(1_000);
            wallet.Data().GetProperty("tier").GetProperty("current").GetProperty("key").GetString().ShouldBe("semente");
            wallet.Data().GetProperty("expiring").GetProperty("cashback").GetProperty("amount").GetInt64().ShouldBe(1_000);
        }

        // 2ª compra usando o saldo: o total cai, e o saldo sai na mesma transação.
        var second = await store.CheckoutAsync(customer, product, useCashback: 1_000);
        second.GetProperty("cashbackUsed").GetInt64().ShouldBe(1_000);
        second.GetProperty("total").GetInt64().ShouldBe(9_000);
        (await store.BalanceAsync(customer)).ShouldBe(0);

        // Cancelado: o saldo volta.
        await store.ChangeStatusAsync(second, "cancelado");
        await DrainAsync();
        (await store.BalanceAsync(customer)).ShouldBe(1_000);

        using var statement = await (await customer.GetAsync("/api/v1/loyalty/me/statement")).ReadJsonAsync();
        statement.Data().GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("reason").GetString())
            .ShouldBe(["reversed", "redeemed", "order_earned"]);
    }

    [Fact]
    public async Task Guest_typing_someone_elses_email_cannot_spend_their_balance()
    {
        var store = await StoreAsync();
        await store.EnableAsync(cashbackPercent: 10, minRedemption: 100);
        var customer = await store.CustomerAsync(Buyer);
        await store.CreditAsync(customer, 5_000);

        var order = await store.CheckoutAsync(store.Shopper, await store.ProductAsync(5), useCashback: 5_000, email: Buyer);

        order.GetProperty("cashbackUsed").GetInt64().ShouldBe(0);
        (await store.BalanceAsync(customer)).ShouldBe(5_000);
    }

    [Fact]
    public async Task Concurrent_checkouts_cannot_spend_the_same_balance_twice()
    {
        var store = await StoreAsync();
        await store.EnableAsync(cashbackPercent: 10, minRedemption: 100);
        var customer = await store.CustomerAsync(Buyer);
        await store.CreditAsync(customer, 3_000);
        var product = await store.ProductAsync(20);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.TryCheckoutAsync(customer, product, useCashback: 3_000)));

        // Quem leu o saldo antes de outro gastá-lo é barrado pela trava da conta (409), nunca gasta em dobro.
        var spent = attempts.Where(a => a.Status == HttpStatusCode.Created).Sum(a => a.CashbackUsed);
        spent.ShouldBe(3_000);
        attempts.ShouldAllBe(a => a.Status == HttpStatusCode.Created || a.Status == HttpStatusCode.Conflict);
        (await store.BalanceAsync(customer)).ShouldBe(0);
    }

    [Fact]
    public async Task Tier_discount_beats_a_smaller_coupon_which_is_not_consumed()
    {
        var store = await StoreAsync();
        await store.EnableAsync(cashbackPercent: 5, minRedemption: 500);
        var customer = await store.CustomerAsync(Buyer);
        await store.CreditAsync(customer, 100);
        await SqlAsync($"UPDATE loyalty_accounts SET tier_key = 'florista_vip' WHERE email = {Buyer}"); // 12%
        var coupon = await store.CouponAsync(percent: 5);

        var order = await store.CheckoutAsync(customer, await store.ProductAsync(5), couponCode: coupon);

        order.GetProperty("tierDiscount").GetInt64().ShouldBe(1_200);
        order.GetProperty("discount").GetInt64().ShouldBe(1_200);
        order.GetProperty("total").GetInt64().ShouldBe(8_800);
        (await ScalarAsync($"SELECT used_count FROM coupons WHERE code = {coupon}")).ShouldBe(0); // não gastou o cupom
    }

    [Fact]
    public async Task Adjustment_needs_a_reason_is_idempotent_and_shows_in_the_liability()
    {
        var store = await StoreAsync();
        await store.EnableAsync(cashbackPercent: 5, minRedemption: 500);
        var customer = await store.CustomerAsync(Buyer);
        var accountId = await store.AccountIdAsync(customer);

        (await store.Admin.PostAsJsonAsync($"/api/v1/loyalty/accounts/{accountId}/adjust", new { kind = "cashback", amount = 500, reason = "" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        for (var i = 0; i < 2; i++)
        {
            (await store.Admin.PostAsJsonAsync($"/api/v1/loyalty/accounts/{accountId}/adjust",
                new { kind = "cashback", amount = 700, reason = "Cortesia de aniversário", idempotencyKey = "aniv-2026" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var liability = await (await store.Admin.GetAsync("/api/v1/loyalty/liability")).ReadJsonAsync();
        liability.Data().GetProperty("cashbackCents").GetInt64().ShouldBe(700);
        (await ScalarAsync($"SELECT count(*) FROM audit_entries WHERE action = 'loyalty.adjusted' AND entity_id = {accountId.ToString()}")).ShouldBe(1);
    }

    [Fact]
    public async Task Expired_cashback_cannot_be_used_and_the_job_writes_it_off()
    {
        var store = await StoreAsync();
        await store.EnableAsync(cashbackPercent: 5, minRedemption: 100);
        var customer = await store.CustomerAsync(Buyer);
        var accountId = await store.CreditAsync(customer, 2_000);
        await SqlAsync($"UPDATE loyalty_entries SET expires_at = now() - interval '1 day' WHERE account_id = {accountId}");

        var order = await store.CheckoutAsync(customer, await store.ProductAsync(5), useCashback: 2_000);
        order.GetProperty("cashbackUsed").GetInt64().ShouldBe(0);

        await factory.Services.GetRequiredService<global::Floruna.Infrastructure.Loyalty.LoyaltyExpiryJob>().RunOnceAsync(CancellationToken.None);
        (await ScalarAsync($"SELECT cashback_balance FROM loyalty_accounts WHERE id = {accountId}")).ShouldBe(0);
        (await ScalarAsync($"SELECT count(*) FROM loyalty_entries WHERE account_id = {accountId} AND kind = 'Expire' AND amount = -2000")).ShouldBe(1);
    }

    [Fact]
    public async Task Loyalty_admin_requires_a_plan_with_the_feature()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        await SqlAsync($"UPDATE tenants SET trial_ends_at = now() - interval '1 day' WHERE slug = {slug}"); // fim do teste grátis: Free
        var admin = factory.ForStore(slug, await factory.LoginStaffAsync(slug, email));

        var response = await admin.GetAsync("/api/v1/loyalty/program");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private async Task DrainAsync()
    {
        var processor = factory.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 100 && await processor.ProcessOnceAsync(CancellationToken.None) > 0; i++)
        {
        }
    }

    private async Task SqlAsync(FormattableString sql)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Database.ExecuteSqlAsync(sql);
    }

    private async Task<long> ScalarAsync(FormattableString sql)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = string.Format(System.Globalization.CultureInfo.InvariantCulture, sql.Format,
            sql.GetArguments().Select((_, i) => (object)$"@p{i}").ToArray());
        foreach (var (value, i) in sql.GetArguments().Select((v, i) => (v, i)))
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"p{i}";
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<Store> StoreAsync()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        return new Store(factory, slug, factory.ForStore(slug, await factory.LoginStaffAsync(slug, email)), factory.ForStore(slug));
    }

    private sealed record Attempt(HttpStatusCode Status, long CashbackUsed);

    private sealed record Store(FlorunaApiFactory Factory, string Slug, HttpClient Admin, HttpClient Shopper)
    {
        public async Task EnableAsync(int cashbackPercent, long minRedemption)
        {
            var response = await Admin.PutAsJsonAsync("/api/v1/loyalty/program", new { enabled = true, cashbackPercent, cashbackMinRedemption = minRedemption });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        public async Task<HttpClient> CustomerAsync(string email)
        {
            (await Shopper.PostAsJsonAsync("/api/v1/auth/register", new { name = "Ana Fiel", email, password = "senha1234", phone = "11999990000" }))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
            using var login = await (await Shopper.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "senha1234" })).ReadJsonAsync();
            return Factory.ForStore(Slug, login.Data().GetProperty("accessToken").GetString());
        }

        public async Task<Guid> AccountIdAsync(HttpClient customer)
        {
            using var wallet = await (await customer.GetAsync("/api/v1/loyalty/me")).ReadJsonAsync();
            return Guid.Parse(wallet.Data().GetProperty("account").GetProperty("id").GetString()!);
        }

        public async Task<long> BalanceAsync(HttpClient customer)
        {
            using var wallet = await (await customer.GetAsync("/api/v1/loyalty/me")).ReadJsonAsync();
            return wallet.Data().GetProperty("account").GetProperty("cashbackBalance").GetInt64();
        }

        /// <summary>Crédito via ajuste do painel (o caminho real de uma cortesia).</summary>
        public async Task<Guid> CreditAsync(HttpClient customer, long amount)
        {
            var accountId = await AccountIdAsync(customer);
            var response = await Admin.PostAsJsonAsync($"/api/v1/loyalty/accounts/{accountId}/adjust", new { kind = "cashback", amount, reason = "Saldo de teste" });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return accountId;
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
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("product").GetProperty("id").GetString()!;
        }

        public async Task<string> CouponAsync(int percent)
        {
            var code = $"CUPOM{Random.Shared.Next(10_000, 99_999)}";
            var response = await Admin.PostAsJsonAsync("/api/v1/coupons", new { code, type = "percent", value = percent, isActive = true });
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return code;
        }

        public async Task<JsonElement> CheckoutAsync(HttpClient client, string productId, long? useCashback = null, string email = Buyer, string? couponCode = null)
        {
            var response = await client.PostAsJsonAsync("/api/v1/orders", Body(productId, useCashback, email, couponCode));
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("order").Clone();
        }

        public async Task<Attempt> TryCheckoutAsync(HttpClient client, string productId, long useCashback)
        {
            var response = await client.PostAsJsonAsync("/api/v1/orders", Body(productId, useCashback, Buyer, null));
            if (response.StatusCode != HttpStatusCode.Created)
            {
                return new Attempt(response.StatusCode, 0);
            }

            using var body = await response.ReadJsonAsync();
            return new Attempt(response.StatusCode, body.Data().GetProperty("order").GetProperty("cashbackUsed").GetInt64());
        }

        public async Task ChangeStatusAsync(JsonElement order, string status)
        {
            var response = await Admin.PatchAsJsonAsync($"/api/v1/orders/{order.GetProperty("id").GetString()}/status", new { status });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        private static object Body(string productId, long? useCashback, string email, string? couponCode) => new
        {
            customerName = "Ana Fiel",
            customerPhone = "(11) 99999-0000",
            customerEmail = email,
            items = new[] { new { productId, quantity = 1 } },
            deliveryMethod = "retirada",
            useCashback,
            couponCode,
        };
    }
}
