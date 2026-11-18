using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Application.Abstractions;
using Floruna.Application.Inventory;
using Floruna.Application.Messaging;
using Floruna.Domain.Inventory;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Inventory;
using Floruna.Infrastructure.Persistence;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Fase 3: estoque com lotes, FEFO, ledger, reservas e concorrência, contra PostgreSQL real.</summary>
[Collection(ApiCollection.Name)]
public sealed class InventoryTests(FlorunaApiFactory factory)
{
    [Fact]
    public async Task New_product_opens_its_stock_with_an_explained_opening_balance()
    {
        var (_, admin) = await AdminAsync();
        var id = await CreateProductAsync(admin, stock: 10);

        using var movements = await (await admin.GetAsync($"/api/v1/stock/{id}/movements")).ReadJsonAsync();
        var opening = movements.Data().GetProperty("movements").EnumerateArray().ShouldHaveSingleItem();
        opening.GetProperty("type").GetString().ShouldBe("opening_balance");
        opening.GetProperty("delta").GetInt32().ShouldBe(10);

        using var lots = await (await admin.GetAsync($"/api/v1/stock/{id}/lots")).ReadJsonAsync();
        lots.Data().GetProperty("lots").EnumerateArray().ShouldHaveSingleItem().GetProperty("quantity").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task Restock_creates_a_lot_and_updates_the_product_cost()
    {
        var (_, admin) = await AdminAsync();
        var id = await CreateProductAsync(admin, stock: 0);

        var response = await admin.PostAsJsonAsync($"/api/v1/stock/{id}/restock", new { quantity = 20, cost = 450, expiresAt = DateTimeOffset.UtcNow.AddDays(6), note = "Ceasa" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await response.ReadJsonAsync();
        body.Data().GetProperty("product").GetProperty("cost").GetInt64().ShouldBe(450);
        body.Data().GetProperty("product").GetProperty("availability").GetProperty("available").GetInt32().ShouldBe(20);
        var movement = body.Data().GetProperty("movement");
        movement.GetProperty("type").GetString().ShouldBe("entrada");
        movement.GetProperty("resultingStock").GetInt32().ShouldBe(20);
        movement.GetProperty("by").GetString().ShouldBe("Dona da Loja");
    }

    [Fact]
    public async Task Expired_units_stay_on_hand_but_are_not_sellable_and_go_first_in_a_loss()
    {
        var (_, admin) = await AdminAsync();
        var id = await CreateProductAsync(admin, stock: 0);
        await admin.PostAsJsonAsync($"/api/v1/stock/{id}/restock", new { quantity = 5, expiresAt = DateTimeOffset.UtcNow.AddDays(-1), note = "murchou" });
        await admin.PostAsJsonAsync($"/api/v1/stock/{id}/restock", new { quantity = 3, expiresAt = DateTimeOffset.UtcNow.AddDays(5) });

        var availability = await AvailabilityAsync(admin, id);
        availability.GetProperty("onHand").GetInt32().ShouldBe(8);
        availability.GetProperty("available").GetInt32().ShouldBe(3);

        using var loss = await (await admin.PostAsJsonAsync($"/api/v1/stock/{id}/loss", new { quantity = 5, note = "descarte" })).ReadJsonAsync();
        loss.Data().GetProperty("movement").GetProperty("delta").GetInt32().ShouldBe(-5);

        availability = await AvailabilityAsync(admin, id);
        availability.GetProperty("onHand").GetInt32().ShouldBe(3);
        availability.GetProperty("available").GetInt32().ShouldBe(3); // o descarte levou os vencidos, não os bons
    }

    [Fact]
    public async Task Stock_never_goes_negative()
    {
        var (_, admin) = await AdminAsync();
        var id = await CreateProductAsync(admin, stock: 2);

        var loss = await admin.PostAsJsonAsync($"/api/v1/stock/{id}/loss", new { quantity = 3 });
        loss.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using (var body = await loss.ReadJsonAsync())
        {
            body.ErrorBody().GetProperty("message").GetString().ShouldBe("Quantidade de perda maior que o estoque disponível.");
        }

        (await admin.PostAsJsonAsync($"/api/v1/stock/{id}/adjust", new { delta = -3 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/v1/stock/{id}/adjust", new { delta = 0 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AvailabilityAsync(admin, id)).GetProperty("onHand").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Editing_stock_in_the_product_form_records_an_adjustment_instead_of_overwriting()
    {
        var (_, admin) = await AdminAsync();
        var id = await CreateProductAsync(admin, stock: 10);

        using var updated = await (await admin.PutAsJsonAsync($"/api/v1/products/{id}", new { stock = 4 })).ReadJsonAsync();
        updated.Data().GetProperty("product").GetProperty("stock").GetInt32().ShouldBe(4);

        using var movements = await (await admin.GetAsync($"/api/v1/stock/{id}/movements")).ReadJsonAsync();
        var latest = movements.Data().GetProperty("movements")[0];
        latest.GetProperty("type").GetString().ShouldBe("ajuste");
        latest.GetProperty("delta").GetInt32().ShouldBe(-6);
    }

    [Fact]
    public async Task Low_stock_and_expiring_alerts()
    {
        var (_, admin) = await AdminAsync();
        var low = await CreateProductAsync(admin, stock: 2, lowStockThreshold: 3);
        var ok = await CreateProductAsync(admin, stock: 50, lowStockThreshold: 3);
        await admin.PostAsJsonAsync($"/api/v1/stock/{ok}/restock", new { quantity = 4, expiresAt = DateTimeOffset.UtcNow.AddDays(2) });

        using (var lowList = await (await admin.GetAsync("/api/v1/stock/low")).ReadJsonAsync())
        {
            lowList.Data().GetProperty("products").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ShouldBe([low]);
        }

        using (var count = await (await admin.GetAsync("/api/v1/stock/low/count")).ReadJsonAsync())
        {
            count.Data().GetProperty("count").GetInt32().ShouldBe(1);
        }

        using var expiring = await (await admin.GetAsync("/api/v1/stock/expiring?days=3")).ReadJsonAsync();
        expiring.Data().GetProperty("lots").EnumerateArray().ShouldHaveSingleItem().GetProperty("productId").GetString().ShouldBe(ok);
    }

    [Fact]
    public async Task Fifty_concurrent_reservations_for_ten_units_never_oversell()
    {
        var (slug, admin) = await AdminAsync();
        var id = Guid.Parse(await CreateProductAsync(admin, stock: 10));
        var tenant = await TenantOfAsync(slug);

        // 50 "clientes" disputando o mesmo produto ao mesmo tempo (RN-20 / RNF-03).
        var attempts = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            await using var scope = factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(
                new ReserveStockCommand(id, 1, ReservationOwnerType.Order, $"pedido-{i}", $"reserva-{i}"));
        }));
        var results = await Task.WhenAll(attempts);

        results.Count(r => r.IsSuccess).ShouldBe(10);
        results.Where(r => r.IsFailure).ShouldAllBe(r => r.Error == InventoryErrors.InsufficientStock);
        var availability = await AvailabilityAsync(admin, id.ToString());
        availability.GetProperty("reserved").GetInt32().ShouldBe(10);
        availability.GetProperty("available").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Reserving_twice_with_the_same_key_holds_only_once()
    {
        var (slug, admin) = await AdminAsync();
        var id = Guid.Parse(await CreateProductAsync(admin, stock: 5));
        var tenant = await TenantOfAsync(slug);

        var first = await ReserveAsync(tenant, new ReserveStockCommand(id, 2, ReservationOwnerType.Order, "pedido-x", "chave-x"));
        var second = await ReserveAsync(tenant, new ReserveStockCommand(id, 2, ReservationOwnerType.Order, "pedido-x", "chave-x"));

        second.Value.Id.ShouldBe(first.Value.Id);
        (await AvailabilityAsync(admin, id.ToString())).GetProperty("reserved").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Releasing_a_reservation_returns_the_units_exactly_once()
    {
        var (slug, admin) = await AdminAsync();
        var id = Guid.Parse(await CreateProductAsync(admin, stock: 5));
        var reservation = await ReserveAsync(await TenantOfAsync(slug), new ReserveStockCommand(id, 3, ReservationOwnerType.Manual, "vitrine", "manual-1"));

        using (var held = await (await admin.GetAsync("/api/v1/stock/reservations")).ReadJsonAsync())
        {
            held.Data().GetProperty("stockReservations").GetArrayLength().ShouldBe(1);
        }

        var release = await admin.PostAsync($"/api/v1/stock/reservations/{reservation.Value.Id}/release", null);
        release.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var body = await release.ReadJsonAsync())
        {
            body.Data().GetProperty("stockReservation").GetProperty("state").GetString().ShouldBe("released");
        }

        var again = await admin.PostAsync($"/api/v1/stock/reservations/{reservation.Value.Id}/release", null);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AvailabilityAsync(admin, id.ToString())).GetProperty("reserved").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Expired_reservations_are_returned_by_the_background_job()
    {
        var (slug, admin) = await AdminAsync();
        var id = Guid.Parse(await CreateProductAsync(admin, stock: 4));
        await ReserveAsync(await TenantOfAsync(slug),
            new ReserveStockCommand(id, 4, ReservationOwnerType.Order, "abandonado", "exp-1", HoldFor: TimeSpan.FromSeconds(-1)));
        (await AvailabilityAsync(admin, id.ToString())).GetProperty("available").GetInt32().ShouldBe(0);

        var processed = await factory.Services.GetRequiredService<ReservationExpiryJob>().RunOnceAsync(CancellationToken.None);

        processed.ShouldBeGreaterThanOrEqualTo(1);
        (await AvailabilityAsync(admin, id.ToString())).GetProperty("available").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task Stock_of_another_store_is_not_reachable()
    {
        var (_, adminA) = await AdminAsync();
        var (_, adminB) = await AdminAsync();
        var productOfA = await CreateProductAsync(adminA, stock: 5);

        (await adminB.PostAsJsonAsync($"/api/v1/stock/{productOfA}/loss", new { quantity = 1 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var movements = await (await adminB.GetAsync($"/api/v1/stock/{productOfA}/movements")).ReadJsonAsync();
        movements.Data().GetProperty("movements").GetArrayLength().ShouldBe(0);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private async Task<(string Slug, HttpClient Admin)> AdminAsync()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        return (slug, factory.ForStore(slug, await factory.LoginStaffAsync(slug, email)));
    }

    private static async Task<string> CreateProductAsync(HttpClient admin, int stock, int lowStockThreshold = 1)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/products", new
        {
            name = $"Produto {Guid.NewGuid():N}"[..20],
            shortDescription = "Uma descrição curta",
            description = "Uma descrição longa o suficiente.",
            category = "buques",
            price = 10_000,
            images = new[] { "https://cdn.test/1.webp" },
            stock,
            lowStockThreshold,
            isActive = true,
            isFeatured = false,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var body = await response.ReadJsonAsync();
        return body.Data().GetProperty("product").GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> AvailabilityAsync(HttpClient admin, string productId)
    {
        using var body = await (await admin.GetAsync("/api/v1/products/manage?limit=100")).ReadJsonAsync();
        return body.Data().GetProperty("products").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == productId)
            .GetProperty("availability").Clone();
    }

    private async Task<ResolvedTenant> TenantOfAsync(string slug)
    {
        using var scope = factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Tenants
            .SingleAsync(t => t.Slug == TenantSlug.FromPersistence(slug));
        return new ResolvedTenant(tenant.Id, slug, tenant.Status, TenantPlan.Pro);
    }

    private async Task<Result<StockReservationResponse>> ReserveAsync(ResolvedTenant tenant, ReserveStockCommand command)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
    }
}
