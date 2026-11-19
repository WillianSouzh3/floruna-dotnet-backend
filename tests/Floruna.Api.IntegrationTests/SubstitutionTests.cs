using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Substituição com consentimento (D-015): nada é aplicado sem autorização válida.</summary>
[Collection(ApiCollection.Name)]
public sealed class SubstitutionTests(FlorunaApiFactory factory)
{
    [Fact]
    public async Task Preapproved_equivalent_swap_is_applied_at_once()
    {
        var store = await StoreAsync();
        var original = await store.ProductAsync(10_000, stock: 5);
        var substitute = await store.ProductAsync(10_500, stock: 5); // +5%: dentro dos 10% da política
        var order = await store.ConfirmedOrderAsync(original, "preapproved");

        var response = await store.SubstituteAsync(order, original, substitute);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = await response.ReadJsonAsync();
        body.Data().GetProperty("substitutionProposal").GetProperty("state").GetString().ShouldBe("applied");
        body.Data().GetProperty("substitutionProposal").GetProperty("decidedBy").GetString().ShouldBe("system");
        body.Data().TryGetProperty("consentToken", out _).ShouldBeFalse();
        var applied = body.Data().GetProperty("order").GetProperty("substitutions").EnumerateArray().ShouldHaveSingleItem();
        applied.GetProperty("substituteProductId").GetString().ShouldBe(substitute);
        (await store.OnHandAsync(substitute)).ShouldBe(4);
        body.Data().GetProperty("order").GetProperty("items")[0].GetProperty("productId").GetString().ShouldBe(original); // o item vendido não muda
    }

    [Fact]
    public async Task Ask_me_waits_for_the_customer_link_and_applies_on_approval()
    {
        var store = await StoreAsync();
        var original = await store.ProductAsync(10_000, stock: 5);
        var substitute = await store.ProductAsync(10_000, stock: 5);
        var order = await store.ConfirmedOrderAsync(original, "ask_me");

        var response = await store.SubstituteAsync(order, original, substitute);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var (proposal, token) = await ProposalAsync(response);
        (await store.OnHandAsync(substitute)).ShouldBe(5); // nada aplicado ainda

        (await store.Admin.PostAsJsonAsync($"/api/v1/substitutions/{proposal}/decision", new { approve = true })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await store.Shopper.GetAsync($"/api/v1/substitutions/{proposal}?token={token}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var consent = await store.Shopper.PostAsJsonAsync($"/api/v1/substitutions/{proposal}/consent", new { token, approve = true });
        consent.StatusCode.ShouldBe(HttpStatusCode.OK, await consent.Content.ReadAsStringAsync());
        using (var body = await consent.ReadJsonAsync())
        {
            body.Data().GetProperty("substitutionProposal").GetProperty("state").GetString().ShouldBe("applied");
        }

        (await store.OnHandAsync(substitute)).ShouldBe(4);
        (await store.Shopper.PostAsJsonAsync($"/api/v1/substitutions/{proposal}/consent", new { token, approve = false })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Exact_orders_refuse_and_preapproval_stops_at_the_tolerance()
    {
        var store = await StoreAsync();
        var original = await store.ProductAsync(10_000, stock: 5);
        var pricier = await store.ProductAsync(15_000, stock: 5); // +50%

        (await store.SubstituteAsync(await store.ConfirmedOrderAsync(original, "exact"), original, pricier)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await store.SubstituteAsync(await store.ConfirmedOrderAsync(original, "preapproved"), original, pricier)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Pending_order_cannot_be_substituted()
    {
        var store = await StoreAsync();
        var original = await store.ProductAsync(10_000, stock: 5);
        var substitute = await store.ProductAsync(10_000, stock: 5);
        var order = await store.OrderAsync(original, "preapproved");

        (await store.SubstituteAsync(order, original, substitute)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approval_survives_missing_stock_and_the_store_applies_later_with_restock_of_the_original()
    {
        var store = await StoreAsync();
        var original = await store.ProductAsync(10_000, stock: 5);
        var substitute = await store.ProductAsync(10_000, stock: 0);
        var order = await store.ConfirmedOrderAsync(original, "ask_me");
        var (proposal, token) = await ProposalAsync(await store.SubstituteAsync(order, original, substitute, restockOriginal: true));

        (await store.Shopper.PostAsJsonAsync($"/api/v1/substitutions/{proposal}/consent", new { token, approve = true })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await store.StateAsync(order, proposal)).ShouldBe("approved"); // aprovada, mas sem estoque para aplicar

        (await store.Admin.PostAsJsonAsync($"/api/v1/stock/{substitute}/restock", new { quantity = 3, note = "Chegou do fornecedor" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var apply = await store.Admin.PostAsync($"/api/v1/substitutions/{proposal}/apply", null);
        apply.StatusCode.ShouldBe(HttpStatusCode.OK, await apply.Content.ReadAsStringAsync());

        (await store.OnHandAsync(substitute)).ShouldBe(2);
        (await store.OnHandAsync(original)).ShouldBe(5); // vendeu 1 e voltou 1 (restockOriginal)
        (await store.Admin.PostAsync($"/api/v1/substitutions/{proposal}/apply", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // uma vez só
    }

    [Fact]
    public async Task Racing_answers_settle_once_and_late_answers_expire()
    {
        var store = await StoreAsync();
        var original = await store.ProductAsync(10_000, stock: 10);
        var substitute = await store.ProductAsync(10_000, stock: 10);
        var (raced, token) = await ProposalAsync(await store.SubstituteAsync(await store.ConfirmedOrderAsync(original, "ask_me"), original, substitute));

        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            store.Shopper.PostAsJsonAsync($"/api/v1/substitutions/{raced}/consent", new { token, approve = i % 2 == 0 })));
        answers.Count(a => a.StatusCode == HttpStatusCode.OK).ShouldBe(1);

        var lateOrder = await store.ConfirmedOrderAsync(original, "ask_me");
        var (late, lateToken) = await ProposalAsync(await store.SubstituteAsync(lateOrder, original, substitute));
        var forgottenOrder = await store.ConfirmedOrderAsync(original, "ask_me");
        var (forgotten, _) = await ProposalAsync(await store.SubstituteAsync(forgottenOrder, original, substitute));
        await SqlAsync($"UPDATE substitution_proposals SET expires_at = now() - interval '1 minute' WHERE id IN ({late}, {forgotten})");

        (await store.Shopper.PostAsJsonAsync($"/api/v1/substitutions/{late}/consent", new { token = lateToken, approve = true })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await store.StateAsync(lateOrder, late)).ShouldBe("expired");
        await factory.Services.GetRequiredService<global::Floruna.Infrastructure.Substitutions.SubstitutionExpiryJob>().RunOnceAsync(CancellationToken.None);
        (await store.StateAsync(forgottenOrder, forgotten)).ShouldBe("expired");
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private static async Task<(Guid Proposal, string Token)> ProposalAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        using var body = await response.ReadJsonAsync();
        return (Guid.Parse(body.Data().GetProperty("substitutionProposal").GetProperty("id").GetString()!), body.Data().GetProperty("consentToken").GetString()!);
    }

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
        public async Task<string> ProductAsync(long price, int stock)
        {
            var response = await Admin.PostAsJsonAsync("/api/v1/products", new
            {
                name = $"Flor {Guid.NewGuid():N}"[..16],
                shortDescription = "Uma descrição curta",
                description = "Uma descrição longa o suficiente.",
                category = "buques",
                price,
                images = new[] { "https://cdn.test/1.webp" },
                stock,
                isActive = true,
                isFeatured = false,
            });
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("product").GetProperty("id").GetString()!;
        }

        public async Task<string> OrderAsync(string productId, string substitutionMode)
        {
            var response = await Shopper.PostAsJsonAsync("/api/v1/orders", new
            {
                customerName = "Ana Cliente",
                customerPhone = "(11) 99999-0000",
                customerEmail = "ana@cliente.com.br",
                items = new[] { new { productId, quantity = 1 } },
                deliveryMethod = "retirada",
                substitutionMode,
            });
            response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            using var body = await response.ReadJsonAsync();
            return body.Data().GetProperty("order").GetProperty("id").GetString()!;
        }

        public async Task<string> ConfirmedOrderAsync(string productId, string substitutionMode)
        {
            var order = await OrderAsync(productId, substitutionMode);
            (await Admin.PatchAsJsonAsync($"/api/v1/orders/{order}/status", new { status = "confirmado" })).StatusCode.ShouldBe(HttpStatusCode.OK);
            return order;
        }

        public Task<HttpResponseMessage> SubstituteAsync(string order, string original, string substitute, bool restockOriginal = false) =>
            Admin.PostAsJsonAsync($"/api/v1/orders/{order}/resolutions/substitution", new
            {
                originalProductId = original,
                substituteProductId = substitute,
                quantity = 1,
                reason = "Faltou a flor original",
                restockOriginal,
            });

        public async Task<string> StateAsync(string order, Guid proposal)
        {
            using var body = await (await Admin.GetAsync($"/api/v1/orders/{order}/substitutions")).ReadJsonAsync();
            return body.Data().GetProperty("substitutionProposals").EnumerateArray().Single(p => p.GetProperty("id").GetString() == proposal.ToString())
                .GetProperty("state").GetString()!;
        }

        public async Task<int> OnHandAsync(string productId)
        {
            using var body = await (await Admin.GetAsync("/api/v1/products/manage?limit=100")).ReadJsonAsync();
            return body.Data().GetProperty("products").EnumerateArray().Single(p => p.GetProperty("id").GetString() == productId)
                .GetProperty("availability").GetProperty("onHand").GetInt32();
        }
    }
}
