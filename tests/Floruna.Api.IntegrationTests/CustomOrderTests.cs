using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Pedido personalizado e orçamento versionado (RN-74).</summary>
[Collection(ApiCollection.Name)]
public sealed class CustomOrderTests(FlorunaApiFactory factory)
{
    [Fact]
    public async Task Request_quote_publish_accept_and_convert_once()
    {
        var store = await StoreAsync();
        var requestId = await store.SubmitAsync();

        var quote = await store.DraftAsync(requestId, unitPrice: 15_000);
        (await store.Admin.PatchAsJsonAsync($"/api/v1/custom-quotes/{quote}", Lines(18_000))).StatusCode.ShouldBe(HttpStatusCode.OK); // rascunho ainda muda
        var token = await store.PublishAsync(quote);

        // O cliente VÊ a proposta pelo link; token errado = "não existe".
        using (var view = await (await store.Shopper.GetAsync($"/api/v1/custom-quotes/{quote}?token={token}")).ReadJsonAsync())
        {
            view.Data().GetProperty("customQuote").GetProperty("total").GetInt64().ShouldBe(18_000);
            view.Data().GetProperty("customQuote").TryGetProperty("responseTokenHash", out _).ShouldBeFalse();
        }

        (await store.Shopper.GetAsync($"/api/v1/custom-quotes/{quote}?token=errado0123456789abc")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await store.Admin.PatchAsJsonAsync($"/api/v1/custom-quotes/{quote}", Lines(1))).StatusCode.ShouldBe(HttpStatusCode.Conflict); // publicado não muda

        (await store.DecideAsync(quote, token, accept: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await store.RequestAsync(requestId)).GetProperty("status").GetString().ShouldBe("approved");
        (await store.RequestAsync(requestId)).GetProperty("quotedPrice").GetInt64().ShouldBe(18_000);

        var order = await store.OrderAsync();
        (await store.ConvertAsync(quote, Guid.NewGuid().ToString())).StatusCode.ShouldBe(HttpStatusCode.NotFound); // pedido inexistente
        var converted = await store.ConvertAsync(quote, order);
        converted.StatusCode.ShouldBe(HttpStatusCode.OK, await converted.Content.ReadAsStringAsync());
        (await store.ConvertAsync(quote, order)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // dois cliques, uma venda
    }

    [Fact]
    public async Task New_version_supersedes_the_published_one_and_nothing_is_published_after_acceptance()
    {
        var store = await StoreAsync();
        var requestId = await store.SubmitAsync();
        var v1 = await store.DraftAsync(requestId, 10_000);
        var token1 = await store.PublishAsync(v1);
        var v2 = await store.DraftAsync(requestId, 12_000);
        var token2 = await store.PublishAsync(v2);

        (await store.DecideAsync(v1, token1, accept: true)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // preço antigo não vale mais
        (await store.DecideAsync(v2, token2, accept: true)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var v3 = await store.DraftAsync(requestId, 9_000);
        (await store.Admin.PostAsJsonAsync($"/api/v1/custom-quotes/{v3}/publish", new { })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var list = await (await store.Admin.GetAsync($"/api/v1/custom-orders/{requestId}/quotes")).ReadJsonAsync();
        list.Data().GetProperty("customQuotes").EnumerateArray().Select(q => (q.GetProperty("version").GetInt32(), q.GetProperty("state").GetString()))
            .ShouldBe([(1, "expired"), (2, "accepted"), (3, "draft")]);
    }

    [Fact]
    public async Task Racing_answers_settle_on_exactly_one_decision()
    {
        var store = await StoreAsync();
        var quote = await store.DraftAsync(await store.SubmitAsync(), 10_000);
        var token = await store.PublishAsync(quote);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.DecideAsync(quote, token, accept: i % 2 == 0)));

        answers.Count(a => a.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        answers.Count(a => a.StatusCode == HttpStatusCode.Conflict).ShouldBe(7);
    }

    [Fact]
    public async Task Concurrent_drafts_get_distinct_consecutive_versions()
    {
        var store = await StoreAsync();
        var requestId = await store.SubmitAsync();

        var drafts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            store.Admin.PostAsJsonAsync($"/api/v1/custom-orders/{requestId}/quotes", Lines(5_000))));

        drafts.ShouldAllBe(d => d.StatusCode == HttpStatusCode.Created);
        using var list = await (await store.Admin.GetAsync($"/api/v1/custom-orders/{requestId}/quotes")).ReadJsonAsync();
        list.Data().GetProperty("customQuotes").EnumerateArray().Select(q => q.GetProperty("version").GetInt32()).ShouldBe([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public async Task Late_answer_expires_the_quote_and_the_job_expires_the_forgotten_ones()
    {
        var store = await StoreAsync();
        var late = await store.DraftAsync(await store.SubmitAsync(), 10_000);
        var token = await store.PublishAsync(late);
        var forgotten = await store.DraftAsync(await store.SubmitAsync(), 10_000);
        await store.PublishAsync(forgotten);
        await SqlAsync($"UPDATE custom_quotes SET valid_until = now() - interval '1 minute' WHERE id IN ({late}, {forgotten})");

        (await store.DecideAsync(late, token, accept: true)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await store.QuoteStateAsync(late)).ShouldBe("expired"); // a expiração foi GRAVADA apesar do erro

        await factory.Services.GetRequiredService<global::Floruna.Infrastructure.CustomOrders.CustomQuoteExpiryJob>().RunOnceAsync(CancellationToken.None);
        (await store.QuoteStateAsync(forgotten)).ShouldBe("expired");
    }

    [Fact]
    public async Task Configurator_values_are_validated()
    {
        var store = await StoreAsync();

        var response = await store.Shopper.PostAsJsonAsync("/api/v1/custom-orders", new
        {
            customerName = "Ana",
            customerPhone = "11999990000",
            containerType = "caixote",
            size = "medio",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await store.Admin.PostAsJsonAsync($"/api/v1/custom-orders/{Guid.NewGuid()}/quotes", Lines(1_000))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private static object Lines(long unitPrice) => new
    {
        lines = new[] { new { description = "Cesta de vime com flores do campo", quantity = 1, unitPrice } },
        notes = "Entrega inclusa",
    };

    private async Task SqlAsync(FormattableString sql)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Database.ExecuteSqlAsync(sql);
    }

    private async Task<Store> StoreAsync()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        return new Store(factory.ForStore(slug, await factory.LoginStaffAsync(slug, email)), factory.ForStore(slug));
    }

    private sealed record Store(HttpClient Admin, HttpClient Shopper)
    {
        public async Task<Guid> SubmitAsync()
        {
            var response = await Shopper.PostAsJsonAsync("/api/v1/custom-orders", new
            {
                customerName = "Ana Cliente",
                customerPhone = "11999990000",
                customerEmail = "ana@cliente.com.br",
                occasion = "aniversario",
                containerType = "cesta_vime",
                size = "grande",
                colorPalette = new[] { "rosa_pastel" },
                flowerPreferences = new[] { "rosas", "lirios" },
                budgetRange = "200_350",
                notes = "Para a minha mãe",
            });
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return Guid.Parse(body.Data().GetProperty("request").GetProperty("id").GetString()!);
        }

        public async Task<JsonElement> RequestAsync(Guid id)
        {
            using var body = await (await Admin.GetAsync($"/api/v1/custom-orders/{id}")).ReadJsonAsync();
            return body.Data().GetProperty("request").Clone();
        }

        public async Task<Guid> DraftAsync(Guid requestId, long unitPrice)
        {
            var response = await Admin.PostAsJsonAsync($"/api/v1/custom-orders/{requestId}/quotes", Lines(unitPrice));
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return Guid.Parse(body.Data().GetProperty("customQuote").GetProperty("id").GetString()!);
        }

        public async Task<string> PublishAsync(Guid quote)
        {
            var response = await Admin.PostAsJsonAsync($"/api/v1/custom-quotes/{quote}/publish", new { validityDays = 7 });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("responseToken").GetString()!;
        }

        public Task<HttpResponseMessage> DecideAsync(Guid quote, string token, bool accept) =>
            Shopper.PostAsJsonAsync($"/api/v1/custom-quotes/{quote}/decision", new { token, accept, reason = accept ? "" : "Achei caro" });

        public Task<HttpResponseMessage> ConvertAsync(Guid quote, string orderId) =>
            Admin.PostAsJsonAsync($"/api/v1/custom-quotes/{quote}/convert", new { orderId });

        public async Task<string> QuoteStateAsync(Guid quote)
        {
            using var body = await (await Admin.GetAsync($"/api/v1/custom-orders/{await RequestOfAsync(quote)}/quotes")).ReadJsonAsync();
            return body.Data().GetProperty("customQuotes").EnumerateArray().Single(q => q.GetProperty("id").GetString() == quote.ToString()).GetProperty("state").GetString()!;
        }

        /// <summary>Um pedido real da loja (o pedido personalizado vira um pedido comum).</summary>
        public async Task<string> OrderAsync()
        {
            var product = await Admin.PostAsJsonAsync("/api/v1/products", new
            {
                name = $"Cesta {Guid.NewGuid():N}"[..18],
                shortDescription = "Uma descrição curta",
                description = "Uma descrição longa o suficiente.",
                category = "buques",
                price = 18_000,
                images = new[] { "https://cdn.test/1.webp" },
                stock = 2,
                isActive = true,
                isFeatured = false,
            });
            using var productBody = await product.ReadJsonAsync();
            var productId = productBody.Data().GetProperty("product").GetProperty("id").GetString();
            var order = await Shopper.PostAsJsonAsync("/api/v1/orders", new
            {
                customerName = "Ana Cliente",
                customerPhone = "(11) 99999-0000",
                customerEmail = "ana@cliente.com.br",
                items = new[] { new { productId, quantity = 1 } },
                deliveryMethod = "retirada",
            });
            using var orderBody = await order.ReadJsonAsync();
            return orderBody.Data().GetProperty("order").GetProperty("id").GetString()!;
        }

        private async Task<string> RequestOfAsync(Guid quote)
        {
            using var page = await (await Admin.GetAsync("/api/v1/custom-orders?limit=100")).ReadJsonAsync();
            foreach (var request in page.Data().GetProperty("requests").EnumerateArray())
            {
                var id = request.GetProperty("id").GetString();
                using var quotes = await (await Admin.GetAsync($"/api/v1/custom-orders/{id}/quotes")).ReadJsonAsync();
                if (quotes.Data().GetProperty("customQuotes").EnumerateArray().Any(q => q.GetProperty("id").GetString() == quote.ToString()))
                {
                    return id!;
                }
            }

            throw new InvalidOperationException("Orçamento sem solicitação.");
        }
    }
}
