using Floruna.Api.IntegrationTests.Support;
using Floruna.Application.Abstractions;
using Floruna.Domain.Customers;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Persistence;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>
/// RNF-07: vazamento entre lojas é impossível POR CONSTRUÇÃO. Estes testes
/// atacam a camada de persistência diretamente, sem passar pela API.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TenantIsolationTests(FlorunaApiFactory factory)
{
    [Fact]
    public async Task Every_store_entity_has_the_tenant_query_filter()
    {
        // Rede de segurança para o futuro: uma entidade nova que implemente
        // ITenantOwned e, por qualquer motivo, fique sem filtro quebra aqui.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();

        var storeEntities = db.Model.GetEntityTypes().Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType)).ToList();

        storeEntities.ShouldNotBeEmpty();
        storeEntities.ShouldAllBe(e => e.GetDeclaredQueryFilters().Count > 0);
    }

    [Fact]
    public async Task A_store_never_sees_another_store_customers()
    {
        var storeA = await CreateTenantAsync();
        var storeB = await CreateTenantAsync();
        await AddCustomerAsync(storeA, "ana@loja-a.com.br");

        using var scope = Scope(storeB);
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();

        (await db.Customers.AnyAsync(c => c.Email == Email.Create("ana@loja-a.com.br").Value)).ShouldBeFalse();
        (await db.Customers.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Querying_store_data_without_a_store_fails_closed()
    {
        using var scope = factory.Services.CreateScope(); // nenhuma loja no contexto
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();

        await Should.ThrowAsync<TenantContextMissingException>(() => db.Customers.ToListAsync());
    }

    [Fact]
    public async Task Inserted_entity_is_stamped_with_the_context_store_not_the_payload()
    {
        var realStore = await CreateTenantAsync();
        var spoofedStore = await CreateTenantAsync();

        using (var scope = Scope(realStore))
        {
            var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
            // O objeto diz "loja falsificada"; o contexto diz "loja real".
            db.Customers.Add(Customer.Register(spoofedStore.Id, "Ana", Email.Create("ana@spoof.com.br").Value, "$hash", null, DateTimeOffset.UtcNow).Value);
            await db.SaveChangesAsync();
        }

        using (var scope = Scope(realStore))
        {
            var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
            (await db.Customers.SingleAsync()).TenantId.ShouldBe(realStore.Id);
        }
    }

    [Fact]
    public async Task Saving_store_data_without_a_store_fails_closed()
    {
        var store = await CreateTenantAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();

        db.Customers.Add(Customer.Register(store.Id, "Ana", Email.Create("ana@sem-loja.com.br").Value, "$hash", null, DateTimeOffset.UtcNow).Value);

        await Should.ThrowAsync<TenantContextMissingException>(() => db.SaveChangesAsync());
    }

    private IServiceScope Scope(ResolvedTenant tenant)
    {
        var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
        return scope;
    }

    private async Task<ResolvedTenant> CreateTenantAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var tenant = Tenant.Create("Loja", TestApi.NewSlug(), DateTimeOffset.UtcNow).Value;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return new ResolvedTenant(tenant.Id, tenant.Slug.Value, tenant.Status, TenantPlan.Free);
    }

    private async Task AddCustomerAsync(ResolvedTenant tenant, string email)
    {
        using var scope = Scope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        db.Customers.Add(Customer.Register(tenant.Id, "Ana", Email.Create(email).Value, "$hash", null, DateTimeOffset.UtcNow).Value);
        await db.SaveChangesAsync();
    }
}
