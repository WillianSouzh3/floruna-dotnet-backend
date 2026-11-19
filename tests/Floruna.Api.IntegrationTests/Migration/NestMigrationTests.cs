using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Floruna.Api.IntegrationTests.Support;
using Floruna.Application.Abstractions;
using Floruna.Application.Payments;
using Floruna.Infrastructure.Persistence;
using Floruna.Migration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Floruna.Api.IntegrationTests.Migration;

/// <summary>
/// ETL Nest (Mongo) → .NET (PostgreSQL), ponta a ponta: semeia um Mongo no FORMATO dos schemas do Nest,
/// migra a loja e confere PELA API .NET que ela funciona (logins antigos, link de pedido, estoque, gateway).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class NestMigrationTests(FlorunaApiFactory factory) : IAsyncLifetime
{
    /// <summary>Hash gerado pelo bcryptjs do próprio Nest (cost 10) para "senha-antiga-do-nest".</summary>
    private const string NestBcryptHash = "$2b$10$d6psnsDqDM1Zm577SQtmxujdjsmFWqEiRsJmHqw.KOJN2d9N2tR6G";
    private const string OldPassword = "senha-antiga-do-nest";
    private const string MercadoPagoToken = "APP_USR-token-da-loja-no-nest";
    private static readonly byte[] NestKey = RandomNumberGenerator.GetBytes(32);

    private readonly MongoDbContainer _mongo = new MongoDbBuilder("mongo:7").Build();

    public Task InitializeAsync() => _mongo.StartAsync();

    public Task DisposeAsync() => _mongo.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrated_store_works_through_the_new_api()
    {
        var seed = await SeedStoreAsync();

        var report = await MigrateAsync(new MigrationRequest(seed.Slug, NestSettingsKey: NestKey));

        report.Committed.ShouldBeTrue(ReportFormatter.Format(report));
        report.Reconciled.ShouldBeTrue();
        report.Imported["produtos"].ShouldBe(2);
        report.Imported["pedidos"].ShouldBe(2);
        var store = factory.ForStore(seed.Slug);

        // Equipe e clientes entram com a senha de SEMPRE (hash bcryptjs do Nest).
        var staffLogin = await store.PostAsJsonAsync("/api/v1/auth/admin/login", new { email = seed.StaffEmail, password = OldPassword });
        staffLogin.StatusCode.ShouldBe(HttpStatusCode.OK, await staffLogin.Content.ReadAsStringAsync());
        using var staffBody = await staffLogin.ReadJsonAsync();
        var admin = factory.ForStore(seed.Slug, staffBody.Data().GetProperty("accessToken").GetString());

        var customerLogin = await store.PostAsJsonAsync("/api/v1/auth/login", new { email = seed.CustomerEmail, password = OldPassword });
        customerLogin.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var customerBody = await customerLogin.ReadJsonAsync();
        var customer = factory.ForStore(seed.Slug, customerBody.Data().GetProperty("accessToken").GetString());
        using (var mine = await (await customer.GetAsync("/api/v1/orders/me")).ReadJsonAsync())
        {
            mine.Data().GetProperty("orders").EnumerateArray().ShouldHaveSingleItem().GetProperty("status").GetString().ShouldBe("entregue");
        }

        // O link de acompanhamento que o cliente recebeu do Nest continua abrindo o pedido.
        var orderId = LegacyIds.For("order", seed.PaidOrder);
        var tracking = await store.GetAsync($"/api/v1/orders/{orderId}?token={seed.AccessToken}");
        tracking.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var order = await tracking.ReadJsonAsync())
        {
            order.Data().GetProperty("order").GetProperty("total").GetInt64().ShouldBe(21_500);
            order.Data().GetProperty("order").GetProperty("paymentStatus").GetString().ShouldBe("approved");
        }

        // Vitrine e estoque: saldo do Nest, lotes com validade (FEFO preservado).
        using (var products = await (await store.GetAsync("/api/v1/products")).ReadJsonAsync())
        {
            products.Data().GetProperty("total").GetInt32().ShouldBe(2);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
            var roses = LegacyIds.For("product", seed.Roses);
            var lots = await db.StockLots.IgnoreQueryFilters().Where(l => l.TenantId == new Floruna.SharedKernel.TenantId(report.TenantId))
                .OrderBy(l => l.ExpiresAt).Select(l => new { l.ProductId, l.Remaining, l.ExpiresAt }).ToListAsync();
            lots.Where(l => l.ProductId.Value == roses).Select(l => l.Remaining).ShouldBe([1, 6]); // saldo 7: fica o lote que vence depois inteiro
            (await db.OutboxMessages.CountAsync(m => m.TenantId == report.TenantId)).ShouldBe(0); // histórico não dispara e-mail

            // Token do gateway: decifrado com a chave do Nest e recifrado com a chave da API .NET.
            var settings = await db.PaymentSettings.IgnoreQueryFilters().SingleAsync(s => s.TenantId == new Floruna.SharedKernel.TenantId(report.TenantId));
            scope.ServiceProvider.GetRequiredService<ISecretProtector>()
                .Unprotect(settings.MercadoPagoTokenCiphertext!, SecretContexts.MercadoPagoToken(settings.TenantId)).ShouldBe(MercadoPagoToken);
        }

        // Pedido pago migrado continua reembolsável (tem intenção de pagamento).
        using var intent = await (await admin.GetAsync($"/api/v1/payments/intents?orderId={orderId}")).ReadJsonAsync();
        intent.Data().GetProperty("paymentIntent").GetProperty("status").GetString().ShouldBe("paid");
        intent.Data().GetProperty("paymentIntent").GetProperty("refundableAmount").GetInt64().ShouldBe(21_500);
    }

    [Fact]
    public async Task Rerun_is_refused_and_replace_rebuilds_with_the_same_ids()
    {
        var seed = await SeedStoreAsync();
        (await MigrateAsync(new MigrationRequest(seed.Slug))).Committed.ShouldBeTrue();

        var again = await MigrateAsync(new MigrationRequest(seed.Slug));
        again.Committed.ShouldBeFalse();
        again.Blockers.ShouldContain(b => b.Contains("--replace", StringComparison.Ordinal));

        var replaced = await MigrateAsync(new MigrationRequest(seed.Slug, Replace: true));
        replaced.Committed.ShouldBeTrue(ReportFormatter.Format(replaced));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlorunaDbContext>();
        var tenant = new Floruna.SharedKernel.TenantId(replaced.TenantId);
        (await db.Products.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenant)).ShouldBe(2); // sem duplicar
        (await db.Products.IgnoreQueryFilters().AnyAsync(p => p.Id == new Domain.Catalog.ProductId(LegacyIds.For("product", seed.Roses)))).ShouldBeTrue();
    }

    [Fact]
    public async Task Dry_run_reports_and_writes_nothing()
    {
        var seed = await SeedStoreAsync();

        var report = await MigrateAsync(new MigrationRequest(seed.Slug, DryRun: true));

        report.Committed.ShouldBeFalse();
        report.Reconciled.ShouldBeTrue();
        report.Imported["pedidos"].ShouldBe(2);
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<FlorunaDbContext>().Tenants.AnyAsync(t => t.Id == new Floruna.SharedKernel.TenantId(report.TenantId))).ShouldBeFalse();
    }

    [Fact]
    public async Task Store_with_pending_orders_or_unported_data_is_not_eligible()
    {
        var pending = await SeedStoreAsync(pendingOrder: true);
        var blocked = await MigrateAsync(new MigrationRequest(pending.Slug));
        blocked.Committed.ShouldBeFalse();
        blocked.Blockers.ShouldContain(b => b.Contains("pendente", StringComparison.Ordinal));

        var loyal = await SeedStoreAsync();
        await Db().GetCollection<BsonDocument>("loyaltyaccounts").InsertOneAsync(new BsonDocument { ["tenantId"] = loyal.Tenant, ["email"] = "a@b.com", ["cashbackBalance"] = 500 });
        (await MigrateAsync(new MigrationRequest(loyal.Slug))).Blockers.ShouldContain(b => b.Contains("fidelidade", StringComparison.Ordinal));

        var forced = await MigrateAsync(new MigrationRequest(loyal.Slug, Force: true));
        forced.Committed.ShouldBeTrue();
        forced.Warnings.ShouldContain(w => w.StartsWith("FICA PARA TRÁS", StringComparison.Ordinal)); // forçar não esconde o que se perde
    }

    [Fact]
    public async Task Record_rejected_by_current_rules_blocks_the_cutover_unless_forced()
    {
        var seed = await SeedStoreAsync();
        // Produto legado sem foto: o domínio de hoje exige ao menos uma imagem.
        await Db().GetCollection<BsonDocument>("products").UpdateOneAsync(
            new BsonDocument("_id", seed.Roses), new BsonDocument("$set", new BsonDocument("images", new BsonArray())));

        var blocked = await MigrateAsync(new MigrationRequest(seed.Slug));
        blocked.Committed.ShouldBeFalse();
        blocked.Rejected.ShouldContain(r => r.Contains("Buquê de Rosas", StringComparison.Ordinal));

        var forced = await MigrateAsync(new MigrationRequest(seed.Slug, Force: true));
        forced.Committed.ShouldBeTrue(ReportFormatter.Format(forced));
        forced.Reconciled.ShouldBeTrue(); // a conferência desconta exatamente o que foi recusado (estoque inclusive)
    }

    [Fact]
    public void Legacy_ids_are_deterministic_and_distinct_per_kind()
    {
        var id = ObjectId.GenerateNewId();

        LegacyIds.For("product", id).ShouldBe(LegacyIds.For("product", id));
        LegacyIds.For("product", id).ShouldNotBe(LegacyIds.For("order", id));
        LegacyIds.For("product", id).ToString()[14].ShouldBe('5'); // UUID versão 5
    }

    // ── Apoio ────────────────────────────────────────────────────────────

    private IMongoDatabase Db() => new MongoClient(_mongo.GetConnectionString()).GetDatabase("flora-lis");

    private async Task<MigrationReport> MigrateAsync(MigrationRequest request)
    {
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var migrator = new NestTenantMigrator(Db(), provider.GetRequiredService<FlorunaDbContext>(), provider.GetRequiredService<TenantContext>(),
            provider.GetRequiredService<ISecretProtector>(), TimeProvider.System);
        return await migrator.MigrateAsync(request, CancellationToken.None);
    }

    private sealed record Seed(string Slug, ObjectId Tenant, string StaffEmail, string CustomerEmail, ObjectId Roses, ObjectId PaidOrder, string AccessToken);

    /// <summary>Documentos no formato dos schemas Mongoose do Nest (nomes de campo, tipos, coleções).</summary>
    private async Task<Seed> SeedStoreAsync(bool pendingOrder = false)
    {
        var db = Db();
        var slug = $"nest-{Guid.NewGuid():N}"[..16];
        var tenant = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;
        await db.GetCollection<BsonDocument>("tenants").InsertOneAsync(new BsonDocument
        {
            ["_id"] = tenant,
            ["name"] = "Flora Migrada",
            ["slug"] = slug,
            ["status"] = "active",
            ["plan"] = "pro",
            ["referralCode"] = $"FLORA-{Guid.NewGuid():N}"[..14].ToUpperInvariant(),
            ["currency"] = "BRL",
            ["timeZone"] = "America/Sao_Paulo",
            ["defaultLocale"] = "pt-BR",
            ["createdAt"] = now.AddYears(-1),
        });
        var staffEmail = $"dona@{slug}.com.br";
        var customerEmail = $"cliente@{slug}.com.br";
        await db.GetCollection<BsonDocument>("adminusers").InsertOneAsync(new BsonDocument
        {
            ["tenantId"] = tenant,
            ["name"] = "Dona",
            ["email"] = staffEmail,
            ["passwordHash"] = NestBcryptHash,
            ["role"] = "master",
            ["sessionVersion"] = 3,
        });
        var customer = ObjectId.GenerateNewId();
        await db.GetCollection<BsonDocument>("customers").InsertOneAsync(new BsonDocument
        {
            ["_id"] = customer,
            ["tenantId"] = tenant,
            ["name"] = "Cliente Fiel",
            ["email"] = customerEmail,
            ["passwordHash"] = NestBcryptHash,
            ["phone"] = "11999990000",
        });
        var category = ObjectId.GenerateNewId();
        await db.GetCollection<BsonDocument>("categories").InsertOneAsync(new BsonDocument
        {
            ["_id"] = category,
            ["tenantId"] = tenant,
            ["name"] = "Buquês",
            ["slug"] = "buques",
            ["description"] = "",
            ["isActive"] = true,
        });
        var roses = ObjectId.GenerateNewId();
        var lilies = ObjectId.GenerateNewId();
        await db.GetCollection<BsonDocument>("products").InsertManyAsync(
        [
            Product(roses, tenant, "Buquê de Rosas", "buque-de-rosas", 10_000, stock: 7, category),
            Product(lilies, tenant, "Arranjo de Lírios", "arranjo-de-lirios", 15_000, stock: 5, category, variants: true),
        ]);
        await db.GetCollection<BsonDocument>("stocklots").InsertManyAsync(
        [
            new BsonDocument { ["tenantId"] = tenant, ["productId"] = roses, ["quantity"] = 3, ["initialQuantity"] = 10, ["expiresAt"] = now.AddDays(2), ["cost"] = 400 },
            new BsonDocument { ["tenantId"] = tenant, ["productId"] = roses, ["quantity"] = 6, ["initialQuantity"] = 6, ["expiresAt"] = now.AddDays(10), ["cost"] = 500 },
        ]);
        var collection = ObjectId.GenerateNewId();
        await db.GetCollection<BsonDocument>("collections").InsertOneAsync(new BsonDocument
        {
            ["_id"] = collection,
            ["tenantId"] = tenant,
            ["name"] = "Dia das Mães",
            ["slug"] = "dia-das-maes",
            ["description"] = "",
            ["isActive"] = true,
            ["startsAt"] = BsonNull.Value,
            ["endsAt"] = BsonNull.Value,
        });
        await db.GetCollection<BsonDocument>("collectionproducts").InsertOneAsync(new BsonDocument { ["tenantId"] = tenant, ["collectionId"] = collection, ["productId"] = roses, ["position"] = 0 });
        await db.GetCollection<BsonDocument>("coupons").InsertOneAsync(new BsonDocument
        {
            ["tenantId"] = tenant,
            ["code"] = "MAES10",
            ["type"] = "percent",
            ["value"] = 10,
            ["minOrder"] = 0,
            ["maxUses"] = 100,
            ["usedCount"] = 3,
            ["isActive"] = true,
            ["targetEmail"] = "",
        });
        await db.GetCollection<BsonDocument>("deliveryzones").InsertOneAsync(new BsonDocument
        {
            ["tenantId"] = tenant,
            ["name"] = "Centro",
            ["zipStart"] = 1000000,
            ["zipEnd"] = 1999999,
            ["fee"] = 1500,
            ["isActive"] = true,
        });
        await db.GetCollection<BsonDocument>("settings").InsertOneAsync(new BsonDocument
        {
            ["tenantId"] = tenant,
            ["paymentGateway"] = "mercadopago",
            ["mercadoPagoOfflineFallbackEnabled"] = true,
            ["pixKey"] = "pix@loja.com.br",
            ["pixReceiverName"] = "Flora Migrada",
            ["pixCity"] = "Sao Paulo",
            ["mercadoPagoAccessToken"] = NestEncrypt(MercadoPagoToken, $"settings.mercadoPagoAccessToken:{tenant}"),
        });
        var paid = ObjectId.GenerateNewId();
        var accessToken = Guid.NewGuid().ToString();
        await db.GetCollection<BsonDocument>("orders").InsertManyAsync(
        [
            Order(paid, tenant, customer, roses, "entregue", "approved", accessToken, now.AddDays(-20), gatewayPaymentId: "123456789"),
            Order(ObjectId.GenerateNewId(), tenant, null, lilies, pendingOrder ? "pendente" : "cancelado", "pending", Guid.NewGuid().ToString(), now.AddDays(-5)),
        ]);
        return new Seed(slug, tenant, staffEmail, customerEmail, roses, paid, accessToken);
    }

    private static BsonDocument Product(ObjectId id, ObjectId tenant, string name, string slug, long price, int stock, ObjectId category, bool variants = false) => new()
    {
        ["_id"] = id,
        ["tenantId"] = tenant,
        ["sku"] = "",
        ["barcode"] = "",
        ["slug"] = slug,
        ["name"] = name,
        ["shortDescription"] = "Flores frescas do dia",
        ["description"] = "Montado na hora com flores da estação.",
        ["category"] = "buques",
        ["categoryIds"] = new BsonArray { category },
        ["price"] = price,
        ["discountPrice"] = BsonNull.Value,
        ["cost"] = 400,
        ["images"] = new BsonArray { "https://cdn.test/flor.webp" },
        ["stock"] = stock,
        ["lowStockThreshold"] = 2,
        ["isActive"] = true,
        ["isFeatured"] = false,
        ["variants"] = variants
            ? new BsonArray { new BsonDocument { ["id"] = $"var_{Guid.NewGuid():N}"[..28], ["label"] = "P", ["price"] = price, ["isActive"] = true }, new BsonDocument { ["id"] = "legado-g", ["label"] = "G", ["price"] = price + 5_000, ["isActive"] = true } }
            : new BsonArray(),
        ["createdAt"] = DateTime.UtcNow.AddMonths(-6),
        ["updatedAt"] = DateTime.UtcNow.AddMonths(-1),
    };

    private static BsonDocument Order(ObjectId id, ObjectId tenant, ObjectId? customer, ObjectId product, string status, string paymentStatus, string accessToken, DateTime created, string? gatewayPaymentId = null) => new()
    {
        ["_id"] = id,
        ["tenantId"] = tenant,
        ["items"] = new BsonArray
        {
            new BsonDocument { ["productId"] = product.ToString(), ["name"] = "Buquê", ["variantLabel"] = BsonNull.Value, ["unitPrice"] = 10_000, ["unitCost"] = 400, ["quantity"] = 2 },
        },
        ["subtotal"] = 20_000,
        ["discount"] = 0,
        ["deliveryFee"] = 1_500,
        ["giftCardFee"] = 0,
        ["total"] = 21_500,
        ["customerName"] = "Cliente Fiel",
        ["customerPhone"] = "(11) 99999-0000",
        ["customerEmail"] = "cliente@fiel.com.br",
        ["customerId"] = customer.HasValue ? customer.Value : BsonNull.Value,
        ["deliveryMethod"] = "entrega",
        ["address"] = new BsonDocument
        {
            ["recipientName"] = "Mãe",
            ["street"] = "Rua das Flores",
            ["number"] = "10",
            ["complement"] = "",
            ["neighborhood"] = "Centro",
            ["city"] = "São Paulo",
            ["state"] = "SP",
            ["zipCode"] = "01001-000",
            ["deliveryNotes"] = "",
        },
        ["deliveryDate"] = created.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        ["deliveryTimeWindow"] = "tarde",
        ["paymentMethod"] = "pix",
        ["substitutionMode"] = "ask_me",
        ["origin"] = "online",
        ["status"] = status,
        ["paymentStatus"] = paymentStatus,
        ["paymentRail"] = gatewayPaymentId is null ? "pix_offline" : "mercadopago",
        ["gatewayPaymentId"] = gatewayPaymentId is null ? BsonNull.Value : gatewayPaymentId,
        ["paymentInitializationStatus"] = "ready",
        ["refundStatus"] = "not_required",
        ["refundedAmount"] = 0,
        ["accessToken"] = accessToken,
        ["pixCode"] = "000201...",
        ["createdAt"] = created,
        ["updatedAt"] = created.AddDays(1),
    };

    /// <summary>Cifra como o SecretEncryptionService do Nest: enc:v1:&lt;key-id&gt;:&lt;iv&gt;:&lt;tag&gt;:&lt;cifrado&gt;.</summary>
    private static string NestEncrypt(string plaintext, string context)
    {
        var iv = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(NestKey, 16))
        {
            aes.Encrypt(iv, data, cipher, tag, Encoding.UTF8.GetBytes(context));
        }

        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var keyId = Convert.ToHexStringLower(SHA256.HashData(NestKey))[..16];
        return $"enc:v1:{keyId}:{B64Url(iv)}:{B64Url(tag)}:{B64Url(cipher)}";
    }
}
