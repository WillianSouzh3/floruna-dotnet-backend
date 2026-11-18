using System.Net;
using System.Net.Http.Json;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Application.Abstractions;
using Floruna.Domain.Identity;
using Floruna.Domain.Platform;
using Floruna.Infrastructure.Mail;
using Floruna.Infrastructure.Outbox;
using Floruna.Infrastructure.Persistence;
using Floruna.SharedKernel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Floruna.Api.IntegrationTests;

/// <summary>Transactional Outbox: atomicidade com o pedido, entrega durável, idempotência por handler e DLQ.</summary>
[Collection(ApiCollection.Name)]
public sealed class OutboxTests(FlorunaApiFactory factory)
{
    [Fact]
    public async Task Order_event_is_written_in_the_same_transaction_and_failed_checkout_writes_none()
    {
        var (slug, admin) = await AdminAsync();
        var product = await ProductAsync(admin, stock: 1);
        var before = await CountAsync("Floruna.Domain.Ordering.OrderPlaced");

        (await factory.ForStore(slug).PostAsJsonAsync("/api/v1/orders", Checkout(product))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await factory.ForStore(slug).PostAsJsonAsync("/api/v1/orders", Checkout(product))).StatusCode.ShouldBe(HttpStatusCode.Conflict); // sem estoque

        (await CountAsync("Floruna.Domain.Ordering.OrderPlaced")).ShouldBe(before + 1); // a transação abortada não deixou evento
    }

    [Fact]
    public async Task Order_email_is_delivered_once_even_if_the_message_is_processed_again()
    {
        var mail = new CapturingEmailTransport();
        using var api = factory.WithEmail(mail);
        await DrainAsync(api);
        var (slug, admin) = await AdminAsync();
        var product = await ProductAsync(admin, stock: 3);
        var buyer = $"comprador-{Guid.NewGuid():N}@teste.com.br";

        (await factory.ForStore(slug).PostAsJsonAsync("/api/v1/orders", Checkout(product, buyer))).StatusCode.ShouldBe(HttpStatusCode.Created);
        await DrainAsync(api);
        (await mail.WaitForAsync(buyer)).Subject.ShouldBe("Recebemos seu pedido");

        // Simula uma réplica que caiu depois de entregar mas antes de marcar como processada.
        await SqlAsync("UPDATE outbox_messages SET state = 'Pending', next_attempt_at = now() WHERE type LIKE '%OrderPlaced' AND state = 'Processed'");
        await DrainAsync(api);

        mail.CountTo(buyer).ShouldBe(1); // o recibo por handler impediu o reenvio
    }

    [Fact]
    public async Task Poison_message_goes_to_the_dead_letter_queue_and_can_be_retried_by_superadmin()
    {
        await DrainAsync(factory.WithEmail(new CapturingEmailTransport()));
        var (slug, admin) = await AdminAsync();
        var product = await ProductAsync(admin, stock: 2);
        (await factory.ForStore(slug).PostAsJsonAsync("/api/v1/orders", Checkout(product))).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var broken = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IEmailTransport, BrokenSmtp>()));
        var processor = broken.Services.GetRequiredService<OutboxProcessor>();

        await processor.ProcessOnceAsync(CancellationToken.None); // 1ª falha: volta para a fila com backoff
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
            var failed = await db.OutboxMessages.SingleAsync(m => m.State == OutboxState.Pending && m.Attempts == 1);
            failed.NextAttemptAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddSeconds(20)); // 30 s de backoff
            failed.LastError.ShouldNotBeNull().ShouldContain("SMTP fora do ar");
        }

        // Pula para a última tentativa permitida.
        await SqlAsync($"UPDATE outbox_messages SET attempts = {OutboxProcessor.MaxAttempts - 1}, next_attempt_at = now() WHERE state = 'Pending'");
        await processor.ProcessOnceAsync(CancellationToken.None);

        var superAdmin = await SuperAdminAsync();
        using var dead = await (await superAdmin.GetAsync("/api/v1/platform/outbox?state=dead")).ReadJsonAsync();
        var message = dead.Data().GetProperty("messages").EnumerateArray().ShouldHaveSingleItem();
        message.GetProperty("type").GetString().ShouldBe("OrderPlaced");

        (await superAdmin.PostAsync($"/api/v1/platform/outbox/{message.GetProperty("id").GetString()}/retry", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountByStateAsync(OutboxState.Dead)).ShouldBe(0);
        await DrainAsync(factory.WithEmail(new CapturingEmailTransport())); // corrigido o SMTP, a mensagem sai
        (await CountByStateAsync(OutboxState.Pending)).ShouldBe(0);
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private sealed class BrokenSmtp : IEmailTransport
    {
        public bool IsConfigured => true;

        public Task DeliverAsync(EmailMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("SMTP fora do ar");
    }

    private static async Task DrainAsync(WebApplicationFactory<Program> api)
    {
        var processor = api.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 100 && await processor.ProcessOnceAsync(CancellationToken.None) > 0; i++)
        {
        }
    }

    private async Task<int> CountAsync(string type)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().OutboxMessages.CountAsync(m => m.Type == type);
    }

    private async Task<int> CountByStateAsync(OutboxState state)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().OutboxMessages.CountAsync(m => m.State == state);
    }

    private async Task SqlAsync(FormattableString sql)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Database.ExecuteSqlAsync(sql);
    }

    private Task SqlAsync(string sql) => SqlAsync(System.Runtime.CompilerServices.FormattableStringFactory.Create(sql));

    private async Task<(string Slug, HttpClient Admin)> AdminAsync()
    {
        var (slug, email) = await factory.SignupStoreAsync();
        return (slug, factory.ForStore(slug, await factory.LoginStaffAsync(slug, email)));
    }

    private async Task<HttpClient> SuperAdminAsync()
    {
        var (slug, _) = await factory.SignupStoreAsync();
        var email = $"suporte@{slug}.com.br";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
            var tenant = await db.Tenants.SingleAsync(t => t.Slug == TenantSlug.FromPersistence(slug));
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(new ResolvedTenant(tenant.Id, slug, tenant.Status, TenantPlan.Free));
            var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(TestApi.StrongPassword);
            db.StaffUsers.Add(StaffUser.Create(tenant.Id, "Suporte", Email.Create(email).Value, hash, Roles.SuperAdmin, DateTimeOffset.UtcNow).Value);
            await db.SaveChangesAsync();
        }

        return factory.ForStore(slug, await factory.LoginStaffAsync(slug, email));
    }

    private static async Task<string> ProductAsync(HttpClient admin, int stock)
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
            isActive = true,
            isFeatured = false,
        });
        using var body = await response.ReadJsonAsync();
        return body.Data().GetProperty("product").GetProperty("id").GetString()!;
    }

    private static object Checkout(string productId, string email = "cliente@teste.com.br") => new
    {
        customerName = "Cliente Teste",
        customerPhone = "(11) 99999-0000",
        customerEmail = email,
        items = new[] { new { productId, quantity = 1 } },
        deliveryMethod = "retirada",
    };
}
