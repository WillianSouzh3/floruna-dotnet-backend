using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Application.Abstractions;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Persistence;
using Floruna.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Fase 2: catálogo de ponta a ponta (produtos, variações, categorias, coleções, cache e planos).</summary>
[Collection(ApiCollection.Name)]
public sealed class CatalogTests(FlorunaApiFactory factory)
{
    // ── Produtos ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_creates_product_and_storefront_sees_it_without_cost()
    {
        var (slug, admin) = await AdminAsync();

        var created = await admin.PostAsJsonAsync("/api/v1/products", Product("Buquê de Rosas", variants: [new { label = "P", price = 15_900 }, new { label = "M", price = 19_900, sku = "buq-m" }]));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var body = await created.ReadJsonAsync();
        var product = body.Data().GetProperty("product");
        product.GetProperty("slug").GetString().ShouldBe("buque-de-rosas");
        product.GetProperty("cost").GetInt64().ShouldBe(8_000);
        product.GetProperty("variants").EnumerateArray().Select(v => v.GetProperty("id").GetString()!)
            .ShouldAllBe(id => id.StartsWith("var_", StringComparison.Ordinal));
        product.GetProperty("availability").GetProperty("available").GetInt32().ShouldBe(10);

        using var publicBody = await (await factory.ForStore(slug).GetAsync("/api/v1/products/buque-de-rosas")).ReadJsonAsync();
        var publicProduct = publicBody.Data().GetProperty("product");
        publicProduct.TryGetProperty("cost", out _).ShouldBeFalse();
        publicProduct.GetProperty("price").GetInt64().ShouldBe(19_900);
        publicProduct.GetProperty("variants").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Discount_not_below_price_is_rejected()
    {
        var (_, admin) = await AdminAsync();

        var response = await admin.PostAsJsonAsync("/api/v1/products", Product("Lírios", price: 10_000, discount: 10_000));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await response.ReadJsonAsync();
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("O preço promocional deve ser menor que o preço.");
    }

    [Fact]
    public async Task Update_is_partial_and_null_discount_removes_the_promotion()
    {
        var (slug, admin) = await AdminAsync();
        var id = await CreateAsync(admin, Product("Orquídea", price: 20_000, discount: 15_000));

        using (var keep = await (await admin.PutAsJsonAsync($"/api/v1/products/{id}", new { name = "Orquídea Branca" })).ReadJsonAsync())
        {
            var p = keep.Data().GetProperty("product");
            p.GetProperty("name").GetString().ShouldBe("Orquídea Branca");
            p.GetProperty("slug").GetString().ShouldBe("orquidea-branca"); // nome mudou, slug acompanha
            p.GetProperty("discountPrice").GetInt64().ShouldBe(15_000);   // ausente = mantém
        }

        using var removed = await (await admin.PutAsJsonAsync($"/api/v1/products/{id}", new { discountPrice = (long?)null })).ReadJsonAsync();
        removed.Data().GetProperty("product").GetProperty("discountPrice").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Duplicate_slug_in_the_same_store_is_rejected()
    {
        var (_, admin) = await AdminAsync();
        await CreateAsync(admin, Product("Cesta Café"));

        var duplicate = await admin.PostAsJsonAsync("/api/v1/products", Product("Cesta Café"));

        duplicate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = await duplicate.ReadJsonAsync();
        body.ErrorBody().GetProperty("message").GetString().ShouldBe("Já existe um produto com o slug \"cesta-cafe\".");
    }

    [Fact]
    public async Task Listing_filters_by_status_search_effective_price_and_paginates()
    {
        var (slug, admin) = await AdminAsync();
        await CreateAsync(admin, Product("Rosa Vermelha", price: 10_000));
        await CreateAsync(admin, Product("Rosa Branca", price: 30_000, discount: 12_000)); // efetivo 120,00
        await CreateAsync(admin, Product("Girassol", price: 25_000));
        await CreateAsync(admin, Product("Rosa Inativa", price: 11_000, isActive: false));
        var store = factory.ForStore(slug);

        (await TotalAsync(store, "?onlyActive=true")).ShouldBe(3);
        (await TotalAsync(store, "?onlyActive=true&search=rosa")).ShouldBe(2);
        (await TotalAsync(store, "?onlyActive=true&minPrice=11000&maxPrice=20000")).ShouldBe(1); // só a Rosa Branca (promo)
        (await TotalAsync(store, "")).ShouldBe(4);

        using var page = await (await store.GetAsync("/api/v1/products?onlyActive=true&limit=2&page=2")).ReadJsonAsync();
        page.Data().GetProperty("products").GetArrayLength().ShouldBe(1);
        page.Data().GetProperty("pages").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Public_listing_is_cached_and_invalidated_on_write()
    {
        var (slug, admin) = await AdminAsync();
        var id = await CreateAsync(admin, Product("Tulipa"));
        var store = factory.ForStore(slug);

        (await FirstNameAsync(store)).ShouldBe("Tulipa"); // popula o cache
        await admin.PutAsJsonAsync($"/api/v1/products/{id}", new { name = "Tulipa Amarela" });

        (await FirstNameAsync(store)).ShouldBe("Tulipa Amarela"); // a escrita invalidou o cache da loja
    }

    [Fact]
    public async Task A_store_never_sees_another_store_products()
    {
        var (storeA, adminA) = await AdminAsync();
        var (storeB, _) = await AdminAsync();
        await CreateAsync(adminA, Product("Exclusivo da Loja A"));

        (await factory.ForStore(storeB).GetAsync("/api/v1/products/exclusivo-da-loja-a")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await TotalAsync(factory.ForStore(storeB), "")).ShouldBe(0);
        (await TotalAsync(factory.ForStore(storeA), "")).ShouldBe(1);
    }

    [Fact]
    public async Task Only_admins_write_the_catalog()
    {
        var (slug, _) = await AdminAsync();

        (await factory.ForStore(slug).PostAsJsonAsync("/api/v1/products", Product("Anônimo"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await factory.ForStore(slug).GetAsync("/api/v1/products/manage")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Free_plan_limits_photos_per_product()
    {
        var (slug, email) = await CreateFreeStoreAsync();
        var admin = factory.ForStore(slug, await factory.LoginStaffAsync(slug, email));

        var response = await admin.PostAsJsonAsync("/api/v1/products", Product("Muitas Fotos", images: 4));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var body = await response.ReadJsonAsync();
        body.ErrorBody().GetProperty("code").GetString().ShouldBe("PLAN_UPGRADE_REQUIRED");
    }

    // ── Categorias ───────────────────────────────────────────────────────

    [Fact]
    public async Task Categories_filter_products_and_cannot_be_deleted_while_in_use()
    {
        var (slug, admin) = await AdminAsync();
        var active = await CreateCategoryAsync(admin, "Buquês Especiais", isActive: true);
        await CreateCategoryAsync(admin, "Rascunho", isActive: false);
        await CreateAsync(admin, Product("Buquê Noiva", categoryIds: [active]));
        await CreateAsync(admin, Product("Vaso Simples"));

        using (var list = await (await factory.ForStore(slug).GetAsync("/api/v1/categories")).ReadJsonAsync())
        {
            list.Data().GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ShouldBe(["buques-especiais"]);
        }

        (await TotalAsync(factory.ForStore(slug), "?category=buques-especiais")).ShouldBe(1);
        (await TotalAsync(factory.ForStore(slug), "?category=rascunho")).ShouldBe(0); // inativa não filtra na vitrine

        var delete = await admin.DeleteAsync($"/api/v1/categories/{active}");
        delete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Inactive_category_cannot_be_assigned()
    {
        var (_, admin) = await AdminAsync();
        var inactive = await CreateCategoryAsync(admin, "Desativada", isActive: false);

        var response = await admin.PostAsJsonAsync("/api/v1/products", Product("Produto X", categoryIds: [inactive]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── Coleções ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Collection_shows_products_in_curated_order_only_inside_its_window()
    {
        var (slug, admin) = await AdminAsync();
        var first = await CreateAsync(admin, Product("Primeiro"));
        var second = await CreateAsync(admin, Product("Segundo"));
        var now = DateTimeOffset.UtcNow;

        var live = await CreateCollectionAsync(admin, "Dia das Mães", now.AddDays(-1), now.AddDays(5));
        await CreateCollectionAsync(admin, "Natal", now.AddDays(30), now.AddDays(40)); // agendada: ainda invisível
        (await admin.PutAsJsonAsync($"/api/v1/collections/{live}/products", new { productIds = new[] { second, first } })).EnsureSuccessStatusCode();

        using (var visible = await (await factory.ForStore(slug).GetAsync("/api/v1/collections")).ReadJsonAsync())
        {
            visible.Data().GetProperty("collections").EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ShouldBe(["dia-das-maes"]);
        }

        using (var page = await (await factory.ForStore(slug).GetAsync("/api/v1/collections/slug/dia-das-maes")).ReadJsonAsync())
        {
            page.Data().GetProperty("products").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(["Segundo", "Primeiro"]);
        }

        (await factory.ForStore(slug).GetAsync("/api/v1/collections/slug/natal")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Apagar um produto tira ele da coleção (FK em cascata).
        (await admin.DeleteAsync($"/api/v1/products/{second}")).EnsureSuccessStatusCode();
        using var after = await (await admin.GetAsync($"/api/v1/collections/{live}")).ReadJsonAsync();
        after.Data().GetProperty("productIds").EnumerateArray().Select(i => i.GetString()).ShouldBe([first]);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private static object Product(
        string name, long price = 19_900, long? discount = null, bool isActive = true, int images = 1,
        object[]? variants = null, string[]? categoryIds = null) => new
        {
            name,
            shortDescription = "Uma descrição curta",
            description = "Uma descrição longa o suficiente.",
            category = "buques",
            categoryIds,
            price,
            discountPrice = discount,
            cost = 8_000,
            images = Enumerable.Range(1, images).Select(i => $"https://cdn.test/{i}.webp").ToArray(),
            stock = 10,
            isActive,
            isFeatured = false,
            variants,
        };

    private async Task<(string Slug, HttpClient Admin)> AdminAsync()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        return (slug, factory.ForStore(slug, await factory.LoginStaffAsync(slug, email)));
    }

    private static async Task<string> CreateAsync(HttpClient admin, object product)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/products", product);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var body = await response.ReadJsonAsync();
        return body.Data().GetProperty("product").GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateCategoryAsync(HttpClient admin, string name, bool isActive)
    {
        using var body = await (await admin.PostAsJsonAsync("/api/v1/categories", new { name, isActive })).ReadJsonAsync();
        return body.Data().GetProperty("category").GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateCollectionAsync(HttpClient admin, string name, DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        using var body = await (await admin.PostAsJsonAsync("/api/v1/collections", new { name, isActive = true, startsAt, endsAt })).ReadJsonAsync();
        return body.Data().GetProperty("collection").GetProperty("id").GetString()!;
    }

    private static async Task<int> TotalAsync(HttpClient client, string query)
    {
        using var body = await (await client.GetAsync($"/api/v1/products{query}")).ReadJsonAsync();
        return body.Data().GetProperty("total").GetInt32();
    }

    private static async Task<string?> FirstNameAsync(HttpClient client)
    {
        using var body = await (await client.GetAsync("/api/v1/products?onlyActive=true")).ReadJsonAsync();
        return body.Data().GetProperty("products")[0].GetProperty("name").GetString();
    }

    /// <summary>Loja no plano grátis SEM teste do Pro (o signup sempre começa com teste).</summary>
    private async Task<(string Slug, string Email)> CreateFreeStoreAsync()
    {
        var slug = TestApi.NewSlug();
        var email = $"dona@{slug}.com.br";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var tenant = Tenant.Create("Loja Grátis", slug, DateTimeOffset.UtcNow).Value;
        db.Tenants.Add(tenant);
        using (scope.ServiceProvider.GetRequiredService<TenantContext>().Enter(new ResolvedTenant(tenant.Id, slug, tenant.Status, TenantPlan.Free)))
        {
            var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(TestApi.StrongPassword);
            db.StaffUsers.Add(StaffUser.Create(tenant.Id, "Dona", Email.Create(email).Value, hash, Roles.Master, DateTimeOffset.UtcNow).Value);
            await db.SaveChangesAsync();
        }

        return (slug, email);
    }
}
