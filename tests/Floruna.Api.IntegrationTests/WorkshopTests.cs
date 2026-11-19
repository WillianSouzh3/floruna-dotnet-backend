using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Oficinas (<c>/events</c>): vagas atômicas, visibilidade e regras de edição.</summary>
[Collection(ApiCollection.Name)]
public sealed class WorkshopTests(FlorunaApiFactory factory)
{
    [Fact]
    public async Task Last_seats_are_never_oversold_under_concurrency()
    {
        var (slug, admin) = await AdminAsync();
        var workshop = await CreateAsync(admin, capacity: 10);
        var id = workshop.GetProperty("id").GetString();
        var shopper = factory.ForStore(slug);

        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            shopper.PostAsJsonAsync($"/api/v1/events/{id}/registrations", new { name = $"Pessoa {i}", phone = "(11) 99999-0000" })));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(10);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(20);
        using var registrations = await (await admin.GetAsync($"/api/v1/events/{id}/registrations?limit=100")).ReadJsonAsync();
        registrations.Data().GetProperty("total").GetInt32().ShouldBe(10); // toda vaga ocupada tem inscrição (mesma transação)
        (await GetAsync(admin, workshop)).GetProperty("registered").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task Registration_returns_the_event_with_the_seat_taken()
    {
        var (slug, admin) = await AdminAsync();
        var workshop = await CreateAsync(admin, capacity: 5);

        var response = await factory.ForStore(slug).PostAsJsonAsync($"/api/v1/events/{workshop.GetProperty("id").GetString()}/registrations",
            new { name = "Ana", phone = "11999990000", email = "Ana@Teste.com.br" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var body = await response.ReadJsonAsync();
        body.Data().GetProperty("event").GetProperty("registered").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Inactive_workshops_are_hidden_from_the_public_and_closed_for_registration()
    {
        var (slug, admin) = await AdminAsync();
        var workshop = await CreateAsync(admin, capacity: 5, isActive: false);
        var shopper = factory.ForStore(slug);

        using (var list = await (await shopper.GetAsync("/api/v1/events")).ReadJsonAsync())
        {
            list.Data().GetProperty("total").GetInt32().ShouldBe(0);
        }

        using (var staffList = await (await admin.GetAsync("/api/v1/events")).ReadJsonAsync())
        {
            staffList.Data().GetProperty("total").GetInt32().ShouldBe(1);
        }

        (await shopper.GetAsync($"/api/v1/events/{workshop.GetProperty("slug").GetString()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await shopper.PostAsJsonAsync($"/api/v1/events/{workshop.GetProperty("id").GetString()}/registrations", new { name = "Ana", phone = "11999990000" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Capacity_cannot_go_below_registrations_and_workshop_with_registrations_cannot_be_deleted()
    {
        var (slug, admin) = await AdminAsync();
        var workshop = await CreateAsync(admin, capacity: 5);
        var id = workshop.GetProperty("id").GetString();
        for (var i = 0; i < 3; i++)
        {
            await factory.ForStore(slug).PostAsJsonAsync($"/api/v1/events/{id}/registrations", new { name = $"Pessoa {i}", phone = "11999990000" });
        }

        (await admin.PutAsJsonAsync($"/api/v1/events/{id}", new { capacity = 2 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var raised = await admin.PutAsJsonAsync($"/api/v1/events/{id}", new { capacity = 8, title = "Oficina de Arranjos Renovada" });
        raised.StatusCode.ShouldBe(HttpStatusCode.OK, await raised.Content.ReadAsStringAsync());
        using (var body = await raised.ReadJsonAsync())
        {
            body.Data().GetProperty("event").GetProperty("slug").GetString().ShouldBe("oficina-de-arranjos-renovada");
            body.Data().GetProperty("event").GetProperty("registered").GetInt32().ShouldBe(3); // a edição não pisou nas inscrições
        }

        (await admin.DeleteAsync($"/api/v1/events/{id}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var empty = await CreateAsync(admin, capacity: 5);
        (await admin.DeleteAsync($"/api/v1/events/{empty.GetProperty("id").GetString()}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Slug_is_unique_per_store_and_events_are_isolated_between_stores()
    {
        var (_, adminA) = await AdminAsync();
        var (slugB, adminB) = await AdminAsync();
        var workshopA = await CreateAsync(adminA, capacity: 5, title: "Oficina de Terrários");

        (await adminA.PostAsJsonAsync("/api/v1/events", Body(5, true, "Oficina de Terrários"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await adminB.PostAsJsonAsync("/api/v1/events", Body(5, true, "Oficina de Terrários"))).StatusCode.ShouldBe(HttpStatusCode.Created); // outra loja, mesmo slug

        (await factory.ForStore(slugB).PostAsJsonAsync($"/api/v1/events/{workshopA.GetProperty("id").GetString()}/registrations", new { name = "Ana", phone = "11999990000" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        (await db.Workshops.IgnoreQueryFilters().SingleAsync(w => w.Id == Guid.Parse(workshopA.GetProperty("id").GetString()!))).Registered.ShouldBe(0);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private async Task<(string Slug, HttpClient Admin)> AdminAsync()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        return (slug, factory.ForStore(slug, await factory.LoginStaffAsync(slug, email)));
    }

    private static object Body(int capacity, bool isActive, string title) => new
    {
        title,
        shortDescription = "Aprenda a montar um arranjo de mesa",
        description = "Uma tarde inteira com flores da estação e café.",
        date = "2026-12-12T14:00",
        location = "Loja Centro",
        image = "https://cdn.test/oficina.webp",
        price = 15_000,
        capacity,
        isActive,
    };

    private static async Task<JsonElement> CreateAsync(HttpClient admin, int capacity, bool isActive = true, string? title = null)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/events", Body(capacity, isActive, title ?? $"Oficina {Guid.NewGuid():N}"[..20]));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var body = await response.ReadJsonAsync();
        return body.Data().GetProperty("event").Clone();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, JsonElement workshop)
    {
        using var body = await (await client.GetAsync($"/api/v1/events/{workshop.GetProperty("slug").GetString()}")).ReadJsonAsync();
        return body.Data().GetProperty("event").Clone();
    }
}
