using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Infrastructure.Outbox;
using Floruna.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Reembolsos: saga com o gateway, recusa x resultado desconhecido, retentativa idempotente, concorrência.</summary>
[Collection(ApiCollection.Name)]
public sealed class RefundTests(FlorunaApiFactory factory)
{
    private const string MpToken = "APP_USR-token-secreto-da-loja";

    [Fact]
    public async Task Cancelling_a_paid_order_refunds_through_the_gateway_first()
    {
        var mp = new FakeMercadoPago();
        using var api = factory.WithMercadoPago(mp);
        var store = await MercadoPagoStoreAsync(api);
        var order = await store.PaidOrderAsync(api, mp);

        var cancel = await store.CancelAsync(order);
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());

        var updated = await store.OrderAsync(order);
        updated.GetProperty("status").GetString().ShouldBe("cancelado");
        updated.GetProperty("paymentStatus").GetString().ShouldBe("refunded");
        updated.GetProperty("refundStatus").GetString().ShouldBe("refunded");
        updated.GetProperty("refundedAmount").GetInt64().ShouldBe(10_000);
        mp.RefundedCents.ShouldBe(10_000);

        var intent = await store.IntentAsync(order);
        intent.GetProperty("paymentIntent").GetProperty("status").GetString().ShouldBe("refunded");
        intent.GetProperty("refunds").EnumerateArray().ShouldHaveSingleItem().GetProperty("method").GetString().ShouldBe("gateway");

        (await store.CancelAsync(order)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // já cancelado
        mp.RefundsExecuted.ShouldBe(1);
    }

    [Fact]
    public async Task Rejected_refund_keeps_the_order_and_releases_the_amount_for_a_new_attempt()
    {
        var mp = new FakeMercadoPago();
        using var api = factory.WithMercadoPago(mp);
        var store = await MercadoPagoStoreAsync(api);
        var order = await store.PaidOrderAsync(api, mp);

        mp.RejectRefunds = true;
        var cancel = await store.CancelAsync(order);
        cancel.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        using (var body = await cancel.ReadJsonAsync())
        {
            body.ErrorBody().GetProperty("code").GetString().ShouldBe("REFUND_NOT_CONFIRMED");
        }

        var kept = await store.OrderAsync(order);
        kept.GetProperty("status").GetString().ShouldBe("confirmado"); // sem dinheiro devolvido, não cancela
        kept.GetProperty("refundStatus").GetString().ShouldBe("failed");
        (await store.IntentAsync(order)).GetProperty("paymentIntent").GetProperty("refundableAmount").GetInt64().ShouldBe(10_000);

        mp.RejectRefunds = false;
        (await store.CancelAsync(order)).StatusCode.ShouldBe(HttpStatusCode.OK); // nova tentativa, nova chave
        (await store.OrderAsync(order)).GetProperty("status").GetString().ShouldBe("cancelado");
        mp.RefundedCents.ShouldBe(10_000);
    }

    [Fact]
    public async Task Lost_gateway_response_is_retried_with_the_same_key_and_never_refunds_twice()
    {
        var mp = new FakeMercadoPago();
        using var api = factory.WithMercadoPago(mp);
        var store = await MercadoPagoStoreAsync(api);
        var order = await store.PaidOrderAsync(api, mp);

        // O gateway PROCESSA, mas a resposta se perde: não dá para saber se o dinheiro saiu.
        mp.LoseRefundResponses = true;
        (await store.CancelAsync(order)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // "em processamento"
        (await store.OrderAsync(order)).GetProperty("status").GetString().ShouldBe("confirmado");
        (await store.CancelAsync(order)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // clicar de novo não reembolsa de novo

        // O gateway volta; o job repete com a MESMA chave.
        mp.LoseRefundResponses = false;
        await SqlAsync("UPDATE refunds SET next_attempt_at = now() - interval '1 second'");
        await api.Services.GetRequiredService<global::Floruna.Infrastructure.Payments.RefundRetryJob>().RunOnceAsync(CancellationToken.None);
        await DrainAsync(api); // RefundSucceeded → cancela o pedido

        mp.RefundsExecuted.ShouldBe(1);
        mp.RefundedCents.ShouldBe(10_000); // devolvido UMA vez
        var updated = await store.OrderAsync(order);
        updated.GetProperty("status").GetString().ShouldBe("cancelado");
        updated.GetProperty("paymentStatus").GetString().ShouldBe("refunded");
    }

    [Fact]
    public async Task Concurrent_partial_refunds_never_exceed_what_was_paid()
    {
        var mp = new FakeMercadoPago();
        using var api = factory.WithMercadoPago(mp);
        var store = await MercadoPagoStoreAsync(api);
        var order = await store.PaidOrderAsync(api, mp);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            store.PartialAsync(order, new { amount = 3_000, reason = "Flores murchas", idempotencyKey = $"murcha-{i}" })));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(3); // 3 × 30,00 cabem em 100,00; o 4º não
        responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest).ShouldBe(2);
        mp.RefundedCents.ShouldBe(9_000);
        (await store.OrderAsync(order)).GetProperty("refundedAmount").GetInt64().ShouldBe(9_000);
        (await store.IntentAsync(order)).GetProperty("paymentIntent").GetProperty("refundableAmount").GetInt64().ShouldBe(1_000);
    }

    [Fact]
    public async Task Manual_pix_refund_with_returned_item_goes_back_to_stock()
    {
        var store = await StoreAsync(factory);
        await store.Admin.PutAsJsonAsync("/api/v1/settings/payments", new { pixKey = "pix@loja.com.br", pixReceiverName = "Flora e Lis", pixCity = "Sao Paulo" });
        var product = await store.ProductAsync(stock: 3);
        var order = await store.CheckoutAsync(product);
        var intentId = (await store.IntentAsync(order)).GetProperty("paymentIntent").GetProperty("id").GetString();
        (await store.Admin.PostAsJsonAsync($"/api/v1/payments/intents/{intentId}/confirm-manual", new
        {
            amount = 10_000,
            evidence = new { type = "pix_receipt", reference = "E1", note = "extrato" },
            idempotencyKey = "manual-1",
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await store.OnHandAsync(product)).ShouldBe(2);

        var tooMany = await store.PartialAsync(order, new { amount = 10_000, reason = "Devolução", items = new[] { new { productId = product, quantity = 2 } }, restock = true });
        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // vendeu 1

        var response = await store.PartialAsync(order, new { amount = 10_000, reason = "Devolução", items = new[] { new { productId = product, quantity = 1 } }, restock = true });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = await response.ReadJsonAsync();
        body.Data().GetProperty("refund").GetProperty("method").GetString().ShouldBe("manual"); // PIX direto: a loja devolve, o sistema registra
        (await store.OnHandAsync(product)).ShouldBe(3);
        (await ScalarAsync($"SELECT count(*) FROM stock_movements WHERE type = 'Devolucao' AND product_id = '{product}'")).ShouldBe(1);
    }

    [Fact]
    public async Task Unpaid_order_cannot_be_refunded_but_can_still_be_cancelled()
    {
        var store = await StoreAsync(factory);
        var order = await store.CheckoutAsync(await store.ProductAsync(stock: 2));

        (await store.PartialAsync(order, new { amount = 1_000, reason = "Teste" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await store.CancelAsync(order)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private static async Task DrainAsync(WebApplicationFactory<Program> api)
    {
        var processor = api.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 100 && await processor.ProcessOnceAsync(CancellationToken.None) > 0; i++)
        {
        }
    }

    private async Task SqlAsync(string sql)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        using var scope = factory.Services.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<Store> StoreAsync(WebApplicationFactory<Program> api)
    {
        var (slug, email) = await factory.SignupStoreAsync();
        return new Store(slug, api.ForStore(slug, await factory.LoginStaffAsync(slug, email)), api.ForStore(slug));
    }

    private async Task<Store> MercadoPagoStoreAsync(WebApplicationFactory<Program> api)
    {
        var store = await StoreAsync(api);
        (await store.Admin.PutAsJsonAsync("/api/v1/settings/payments", new { paymentGateway = "mercadopago", mercadoPagoAccessToken = MpToken }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        return store;
    }

    private sealed record Store(string Slug, HttpClient Admin, HttpClient Shopper)
    {
        public async Task<JsonElement> PaidOrderAsync(WebApplicationFactory<Program> api, FakeMercadoPago mp)
        {
            var order = await CheckoutAsync(await ProductAsync(stock: 3));
            var providerId = (await IntentAsync(order)).GetProperty("paymentIntent").GetProperty("providerPaymentId").GetString()!;
            mp.SetStatus(providerId, "approved");
            await api.CreateClient().PostAsJsonAsync($"/api/v1/payments/webhook?tenant={Slug}",
                new { type = "payment", data = new { id = long.Parse(providerId, System.Globalization.CultureInfo.InvariantCulture) } });
            await DrainAsync(api);
            (await OrderAsync(order)).GetProperty("status").GetString().ShouldBe("confirmado");
            return order;
        }

        public Task<HttpResponseMessage> CancelAsync(JsonElement order) =>
            Admin.PatchAsJsonAsync($"/api/v1/orders/{order.GetProperty("id").GetString()}/status", new { status = "cancelado", reason = "Cliente desistiu" });

        public Task<HttpResponseMessage> PartialAsync(JsonElement order, object body) =>
            Admin.PostAsJsonAsync($"/api/v1/orders/{order.GetProperty("id").GetString()}/resolutions/refund", body);

        public async Task<JsonElement> IntentAsync(JsonElement order)
        {
            using var body = await (await Admin.GetAsync($"/api/v1/payments/intents?orderId={order.GetProperty("id").GetString()}")).ReadJsonAsync();
            return body.Data().Clone();
        }

        public async Task<JsonElement> OrderAsync(JsonElement order)
        {
            using var body = await (await Admin.GetAsync($"/api/v1/orders/{order.GetProperty("id").GetString()}")).ReadJsonAsync();
            return body.Data().GetProperty("order").Clone();
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

        public async Task<JsonElement> CheckoutAsync(string productId)
        {
            var response = await Shopper.PostAsJsonAsync("/api/v1/orders", new
            {
                customerName = "Ana Compradora",
                customerPhone = "(11) 99999-0000",
                customerEmail = "ana@compradora.com.br",
                items = new[] { new { productId, quantity = 1 } },
                deliveryMethod = "retirada",
            });
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("order").Clone();
        }
    }
}
