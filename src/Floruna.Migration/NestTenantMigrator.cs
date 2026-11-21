using System.Security.Cryptography;
using System.Text;
using Floruna.Application.Abstractions;
using Floruna.Application.Payments;
using Floruna.Domain.Catalog;
using Floruna.Domain.Customers;
using Floruna.Domain.Delivery;
using Floruna.Domain.Identity;
using Floruna.Domain.Inventory;
using Floruna.Domain.Ordering;
using Floruna.Domain.Payments;
using Floruna.Domain.Platform;
using Floruna.Domain.Promotions;
using Floruna.Infrastructure.Persistence;
using Floruna.SharedKernel;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Floruna.Migration;

/// <param name="TenantSlug">Loja a migrar (a unidade de corte é a LOJA, ver ADR 0016).</param>
/// <param name="DryRun">Faz tudo e desfaz no fim: ensaio com relatório, sem gravar nada.</param>
/// <param name="Replace">Apaga a loja no PostgreSQL antes (re-ensaio). Sem isso, loja já existente é recusada.</param>
/// <param name="Force">Migra mesmo com dados em módulos que este ETL não importa (eles ficam para trás).</param>
/// <param name="NestSettingsKey">SETTINGS_ENCRYPTION_KEY do Nest (base64 de 32 bytes), para trazer o token do gateway.</param>
public sealed record MigrationRequest(string TenantSlug, bool DryRun = false, bool Replace = false, bool Force = false, byte[]? NestSettingsKey = null);

public sealed class MigrationReport
{
    public string Tenant { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    public bool Committed { get; set; }

    public bool DryRun { get; set; }

    public SortedDictionary<string, int> Imported { get; } = new(StringComparer.Ordinal);

    public List<string> Blockers { get; } = [];

    public List<string> Warnings { get; } = [];

    /// <summary>Registros que NÃO passaram nas regras atuais do domínio (dado que ficaria para trás).</summary>
    public List<string> Rejected { get; } = [];

    internal HashSet<ObjectId> RejectedProducts { get; } = [];

    internal HashSet<ObjectId> RejectedOrders { get; } = [];

    /// <summary>Conferência origem x destino (contagens e somas). Toda linha precisa bater.</summary>
    public List<(string Check, long Source, long Target)> Reconciliation { get; } = [];

    public bool Reconciled => Reconciliation.All(r => r.Source == r.Target);

    internal void Count(string what) => Imported[what] = Imported.GetValueOrDefault(what) + 1;
}

/// <summary>
/// ETL de UMA loja: Mongo do Nest → PostgreSQL da API .NET, numa transação. Ordem de dependência:
/// loja → equipe → clientes → catálogo → estoque (saldo de abertura com os lotes, FEFO preservado) → cupons →
/// zonas → pagamento → pedidos (histórico + intenção de pagamento dos pagos). No fim, CONFERE origem x destino;
/// se algo não bater, desfaz tudo.
/// </summary>
public sealed class NestTenantMigrator(IMongoDatabase mongo, FlorunaDbContext db, TenantContext tenantContext, ISecretProtector secrets, TimeProvider clock)
{
    /// <summary>Coleções com DADO OPERACIONAL de módulos que este ETL não importa: perder isso é perder dado do lojista.</summary>
    private static readonly IReadOnlyDictionary<string, string> BlockingCollections = new Dictionary<string, string>
    {
        ["loyaltyaccounts"] = "fidelidade (saldos de cashback)",
        ["loyaltyentries"] = "fidelidade (extrato)",
        ["events"] = "oficinas",
        ["registrations"] = "inscrições em oficinas",
        ["custom_order_requests"] = "pedidos personalizados",
        ["customquotes"] = "orçamentos",
        ["cashsessions"] = "caixas do PDV",
        ["substitutionproposals"] = "propostas de substituição",
        ["campaigns"] = "campanhas",
        ["reviews"] = "avaliações",
        ["assemblyjobs"] = "fila de montagem",
        ["compositiontemplates"] = "composições",
        ["goodsreceipts"] = "financeiro (entradas de mercadoria)",
        ["fulfillments"] = "expedição",
        ["deliverytasks"] = "entregas (rotas)",
        ["pickuppoints"] = "pontos de retirada",
        ["flowersubscriptions"] = "assinaturas de flores",
        ["customerreminders"] = "CRM (lembretes)",
    };

    /// <summary>Configuração que pode existir com valores padrão: aviso, não bloqueio.</summary>
    private static readonly IReadOnlyDictionary<string, string> WarningCollections = new Dictionary<string, string>
    {
        ["loyaltyprograms"] = "programa de fidelidade",
        ["substitutionpolicies"] = "política de substituição",
        ["fiscalprofiles"] = "perfil fiscal",
        ["whatsappconfigs"] = "WhatsApp",
        ["financialcategories"] = "categorias financeiras",
        ["deliverycapacityrules"] = "regras de capacidade de entrega",
    };

    public async Task<MigrationReport> MigrateAsync(MigrationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var report = new MigrationReport { Tenant = request.TenantSlug, DryRun = request.DryRun };
        var source = await Collection("tenants").Find(new BsonDocument("slug", request.TenantSlug)).FirstOrDefaultAsync(ct);
        if (source is null)
        {
            report.Blockers.Add($"Loja \"{request.TenantSlug}\" não existe no Mongo.");
            return report;
        }

        var legacyTenant = source.Id();
        var tenantId = new TenantId(LegacyIds.For("tenant", legacyTenant));
        report.TenantId = tenantId.Value;
        await CheckEligibilityAsync(legacyTenant, request, report, ct);
        // No ensaio, bloqueio de elegibilidade NÃO interrompe: o ensaio importa tudo (e desfaz) para mostrar
        // de uma vez o que mais daria errado (recusas, conferência). No corte de verdade, para aqui.
        if (report.Blockers.Count > 0 && !request.DryRun)
        {
            return report;
        }

        var exists = await db.Tenants.AnyAsync(t => t.Id == tenantId, ct);
        if (exists && !request.Replace)
        {
            report.Blockers.Add("A loja já existe no PostgreSQL. Use --replace para refazer (re-ensaio).");
            return report;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (exists)
        {
            await DeleteTenantAsync(tenantId, ct);
        }

        var now = clock.GetUtcNow();
        var tenant = ImportTenant(source, tenantId, report);
        // Código de indicação é único NA PLATAFORMA: se o do Nest já existe aqui (outra loja), fica o gerado.
        if (await db.Tenants.AnyAsync(t => t.ReferralCode == tenant.ReferralCode && t.Id != tenantId, ct))
        {
            report.Warnings.Add($"Código de indicação {tenant.ReferralCode} já existe na plataforma nova: a loja recebeu outro.");
            Restore.Set(tenant, nameof(Tenant.ReferralCode), Tenant.Create(tenant.Name, tenant.Slug.Value, now).Value.ReferralCode);
        }

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);
        using (tenantContext.Enter(new ResolvedTenant(tenantId, tenant.Slug.Value, tenant.Status, tenant.EffectivePlan(now), tenant.TimeZone)))
        {
            var filter = new BsonDocument("tenantId", legacyTenant);
            await ImportStaffAsync(filter, tenantId, report, ct);
            var customers = await ImportCustomersAsync(filter, tenantId, report, ct);
            await ImportCatalogAsync(filter, tenantId, report, ct);
            await ImportCouponsAndZonesAsync(filter, tenantId, report, ct);
            await ImportPaymentSettingsAsync(filter, legacyTenant, tenantId, request.NestSettingsKey, report, ct);
            await ImportOrdersAsync(filter, tenantId, customers, report, ct);
            ClearDomainEvents(); // histórico importado não dispara e-mail, cashback nem nada
            await db.SaveChangesAsync(ct);
            await ReconcileAsync(filter, report, ct);
        }

        // Registro recusado pelas regras de hoje = dado que ficaria para trás. Sem --force, não grava nada:
        // a pessoa corrige na origem (ou aceita a perda conscientemente) e roda de novo.
        if (report.Rejected.Count > 0 && !request.Force)
        {
            report.Blockers.Add($"{report.Rejected.Count} registro(s) recusado(s) pelas regras atuais (ver lista). Corrija na origem ou use --force.");
            await transaction.RollbackAsync(ct);
            return report;
        }

        if (!report.Reconciled)
        {
            report.Blockers.Add("A conferência origem x destino não bateu: nada foi gravado.");
            await transaction.RollbackAsync(ct);
            return report;
        }

        if (request.DryRun)
        {
            await transaction.RollbackAsync(ct);
            report.Warnings.Add("Ensaio (--dry-run): tudo foi processado e desfeito.");
            return report;
        }

        await transaction.CommitAsync(ct);
        report.Committed = true;
        return report;
    }

    // ── Elegibilidade ──

    private async Task CheckEligibilityAsync(ObjectId tenant, MigrationRequest request, MigrationReport report, CancellationToken ct)
    {
        var filter = new BsonDocument("tenantId", tenant);
        // Pedido pendente tem estoque "em trânsito" entre os dois sistemas: espere pagar ou abandonar (3 h).
        var pending = await Collection("orders").CountDocumentsAsync(With(filter, new BsonDocument("status", "pendente")), cancellationToken: ct);
        if (pending > 0)
        {
            report.Blockers.Add($"{pending} pedido(s) pendente(s): aguarde pagarem ou serem abandonados antes do corte.");
        }

        foreach (var (collection, what) in BlockingCollections)
        {
            var count = await Collection(collection).CountDocumentsAsync(filter, cancellationToken: ct);
            if (count > 0)
            {
                var message = $"{count} registro(s) de {what} ({collection}): este ETL ainda não importa esse módulo.";
                (request.Force ? report.Warnings : report.Blockers).Add(request.Force ? $"FICA PARA TRÁS: {message}" : message);
            }
        }

        foreach (var (collection, what) in WarningCollections)
        {
            if (await Collection(collection).CountDocumentsAsync(filter, cancellationToken: ct) > 0)
            {
                report.Warnings.Add($"Configuração de {what} ({collection}) não é migrada: reconfigure no painel novo.");
            }
        }
    }

    // ── Loja, equipe, clientes ──

    private Tenant ImportTenant(BsonDocument d, TenantId id, MigrationReport report)
    {
        var created = d.Date("createdAt") ?? clock.GetUtcNow();
        var tenant = Tenant.Create(d.Str("name"), d.Str("slug"), created).Value;
        Restore.Set(tenant, "Id", id);
        Restore.Set(tenant, nameof(Tenant.Status), d.Str("status") == "suspended" ? TenantStatus.Suspended : TenantStatus.Active);
        Restore.Set(tenant, nameof(Tenant.Plan), d.Str("plan") switch
        {
            "essential" => TenantPlan.Essential,
            "pro" => TenantPlan.Pro,
            "complete" => TenantPlan.Complete,
            _ => TenantPlan.Free,
        });
        Restore.Set(tenant, nameof(Tenant.TrialEndsAt), d.Date("trialEndsAt"));
        Restore.Set(tenant, nameof(Tenant.PastDueSince), d.Date("pastDueSince"));
        Restore.Set(tenant, nameof(Tenant.ReferralCode), d.Str("referralCode", tenant.ReferralCode));
        Restore.Set(tenant, nameof(Tenant.Currency), d.Str("currency", "BRL"));
        Restore.Set(tenant, nameof(Tenant.TimeZone), d.Str("timeZone", "America/Sao_Paulo"));
        Restore.Set(tenant, nameof(Tenant.Locale), d.Str("defaultLocale", "pt-BR"));
        if (d.NStr("customDomain") is { } domain)
        {
            Restore.Set(tenant, nameof(Tenant.CustomDomain), domain.ToLowerInvariant());
            Restore.Set(tenant, nameof(Tenant.CustomDomainVerified), d.Str("customDomainStatus") == "verified");
            Restore.Set(tenant, nameof(Tenant.CustomDomainVerifiedAt), d.Date("customDomainVerifiedAt"));
            Restore.Set(tenant, nameof(Tenant.CustomDomainToken), d.NStr("customDomainToken"));
        }

        tenant.ClearDomainEvents();
        report.Count("lojas");
        return tenant;
    }

    private async Task ImportStaffAsync(BsonDocument filter, TenantId tenantId, MigrationReport report, CancellationToken ct)
    {
        foreach (var d in await Collection("adminusers").Find(filter).ToListAsync(ct))
        {
            var email = Email.Create(d.Str("email"));
            // O hash BCrypt do Nest (bcryptjs) vale aqui: a senha continua a mesma (ADR 0002).
            var created = email.IsSuccess ? StaffUser.Create(tenantId, d.Str("name"), email.Value, d.Str("passwordHash"), d.Str("role"), d.Date("createdAt") ?? clock.GetUtcNow()) : email.Error;
            if (Skip(created, $"usuário {d.Str("email")}", report))
            {
                continue;
            }

            Restore.Set(created.Value, "Id", new StaffUserId(LegacyIds.For("adminuser", d.Id())));
            db.StaffUsers.Add(created.Value);
            report.Count("equipe");
        }
    }

    private async Task<Dictionary<ObjectId, Guid>> ImportCustomersAsync(BsonDocument filter, TenantId tenantId, MigrationReport report, CancellationToken ct)
    {
        var ids = new Dictionary<ObjectId, Guid>();
        foreach (var d in await Collection("customers").Find(filter).ToListAsync(ct))
        {
            var email = Email.Create(d.Str("email"));
            var created = email.IsSuccess ? Customer.Register(tenantId, d.Str("name"), email.Value, d.Str("passwordHash"), d.NStr("phone"), d.Date("createdAt") ?? clock.GetUtcNow()) : email.Error;
            if (Skip(created, $"cliente {d.Str("email")}", report))
            {
                continue;
            }

            var id = LegacyIds.For("customer", d.Id());
            Restore.Set(created.Value, "Id", new CustomerId(id));
            created.Value.ClearDomainEvents();
            db.Customers.Add(created.Value);
            ids[d.Id()] = id;
            report.Count("clientes");
        }

        return ids;
    }

    // ── Catálogo e estoque ──

    private async Task ImportCatalogAsync(BsonDocument filter, TenantId tenantId, MigrationReport report, CancellationToken ct)
    {
        foreach (var d in await Collection("categories").Find(filter).ToListAsync(ct))
        {
            var created = Category.Create(tenantId, d.Str("name"), d.NStr("slug"), d.NStr("description"), d.Bool("isActive", true));
            if (!Skip(created, $"categoria {d.Str("name")}", report))
            {
                Restore.Set(created.Value, "Id", new CategoryId(LegacyIds.For("category", d.Id())));
                db.Categories.Add(created.Value);
                report.Count("categorias");
            }
        }

        var lotsByProduct = (await Collection("stocklots").Find(With(filter, new BsonDocument("quantity", new BsonDocument("$gt", 0)))).ToListAsync(ct))
            .Where(l => l.Oid("productId") is not null)
            .GroupBy(l => l.Oid("productId")!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var d in await Collection("products").Find(filter).ToListAsync(ct))
        {
            var draft = new ProductDraft(
                d.Str("name"), d.NStr("slug"), d.Str("shortDescription"), d.Str("description"), ParseCategory(d.Str("category")),
                [.. d.Strings("categoryIds").Concat(CategoryIds(d)).Distinct().Select(oid => new CategoryId(LegacyIds.For("category", oid)))],
                d.Long("price"), d.NLong("discountPrice"), d.NLong("cost"), [.. d.Strings("images")], d.Int("lowStockThreshold", 5),
                d.Bool("isActive", true), d.Bool("isFeatured"), d.NStr("sku"), d.NStr("barcode"),
                [.. d.Docs("variants").Select(v => new VariantDraft(VariantIdFor(d, v, report), v.NStr("sku"), v.Str("label"), v.Long("price"), v.Bool("isActive", true)))]);
            var created = Product.Create(tenantId, draft, d.Date("createdAt") ?? clock.GetUtcNow());
            if (Skip(created, $"produto {d.Str("name")}", report))
            {
                report.RejectedProducts.Add(d.Id());
                continue;
            }

            var product = created.Value;
            var productId = new ProductId(LegacyIds.For("product", d.Id()));
            Restore.Set(product, "Id", productId);
            Restore.Set(product, nameof(Product.UpdatedAt), d.Date("updatedAt") ?? product.UpdatedAt);
            product.ClearDomainEvents();
            db.Products.Add(product);
            report.Count("produtos");
            ImportStock(productId, tenantId, product.Name, d.Int("stock"), d.NLong("cost"), lotsByProduct.GetValueOrDefault(d.Id(), []), report);
        }

        foreach (var d in await Collection("collections").Find(filter).ToListAsync(ct))
        {
            var created = Domain.Catalog.Collection.Create(tenantId, d.Str("name"), d.NStr("slug"), d.NStr("description"), d.Bool("isActive", true), d.Date("startsAt"), d.Date("endsAt"));
            if (Skip(created, $"coleção {d.Str("name")}", report))
            {
                continue;
            }

            Restore.Set(created.Value, "Id", new CollectionId(LegacyIds.For("collection", d.Id())));
            var memberships = (await Collection("collectionproducts").Find(With(filter, new BsonDocument("collectionId", d.Id()))).ToListAsync(ct))
                .OrderBy(m => m.Int("position")).ToList();
            var members = memberships.Where(m => m.Oid("productId") is { } p && !report.RejectedProducts.Contains(p))
                .Select(m => new ProductId(LegacyIds.For("product", m.Oid("productId")!.Value))).ToList();
            if (members.Count < memberships.Count)
            {
                report.Warnings.Add($"Coleção {d.Str("name")}: {memberships.Count - members.Count} produto(s) recusado(s) ficaram de fora.");
            }

            created.Value.ReplaceProducts(members);
            db.Collections.Add(created.Value);
            report.Count("coleções");
        }
    }

    /// <summary>
    /// Saldo de abertura. A verdade do saldo é <c>product.stock</c> (o que o Nest vendia); os LOTES vêm de
    /// <c>stocklots</c> com validade e custo, para o FEFO continuar valendo. Se os lotes somam mais que o saldo,
    /// ficam os de validade mais LONGA (os que vencem antes já teriam saído pelo FEFO); se somam menos, a
    /// diferença vira um lote de abertura sem validade.
    /// </summary>
    private void ImportStock(ProductId productId, TenantId tenantId, string name, int onHand, long? cost, List<BsonDocument> lots, MigrationReport report)
    {
        var item = StockItem.Open(productId, tenantId);
        db.StockItems.Add(item);
        var now = clock.GetUtcNow();
        var remaining = Math.Max(0, onHand);
        var ordered = lots.OrderByDescending(l => l.Date("expiresAt") ?? DateTimeOffset.MaxValue).ToList();
        if (ordered.Sum(l => l.Long("quantity")) > remaining)
        {
            report.Warnings.Add($"{name}: lotes somam mais que o saldo ({ordered.Sum(l => l.Long("quantity"))} > {onHand}); mantidos os de validade mais longa.");
        }

        foreach (var lot in ordered)
        {
            var quantity = (int)Math.Min(lot.Long("quantity"), remaining);
            if (quantity <= 0)
            {
                break;
            }

            Receive(item, tenantId, productId, name, quantity, lot.Date("expiresAt"), lot.NLong("cost") ?? cost, $"import:lot:{lot.Id()}", now);
            remaining -= quantity;
        }

        if (remaining > 0)
        {
            Receive(item, tenantId, productId, name, remaining, null, cost, $"import:opening:{productId}", now);
        }

        report.Count("estoques");
    }

    private void Receive(StockItem item, TenantId tenantId, ProductId productId, string name, int quantity, DateTimeOffset? expiresAt, long? cost, string key, DateTimeOffset now)
    {
        var change = item.Receive(quantity, expiresAt, cost, "Saldo migrado do sistema anterior", "migracao", now, StockMovementType.OpeningBalance);
        db.StockMovements.Add(StockMovement.Record(tenantId, productId, name, change, "Saldo migrado do sistema anterior", "migracao", now, null, key));
    }

    // ── Cupons, zonas, pagamento ──

    private async Task ImportCouponsAndZonesAsync(BsonDocument filter, TenantId tenantId, MigrationReport report, CancellationToken ct)
    {
        foreach (var d in await Collection("coupons").Find(filter).ToListAsync(ct))
        {
            var type = d.Str("type") switch { "fixed" => CouponType.Fixed, "free_shipping" => CouponType.FreeShipping, _ => CouponType.Percent };
            var created = Coupon.Create(tenantId, new CouponSpec(d.Str("code"), type, d.Long("value"), d.Long("minOrder"), d.NLong("maxUses") is { } max ? (int)max : null,
                d.NLong("perCustomerLimit") is { } per ? (int)per : null, d.Date("expiresAt"), d.NStr("targetEmail"), d.Bool("isActive", true)), d.Date("createdAt") ?? clock.GetUtcNow());
            if (!Skip(created, $"cupom {d.Str("code")}", report))
            {
                Restore.Set(created.Value, "Id", LegacyIds.For("coupon", d.Id()));
                Restore.Set(created.Value, nameof(Coupon.UsedCount), d.Int("usedCount"));
                db.Coupons.Add(created.Value);
                report.Count("cupons");
            }
        }

        foreach (var d in await Collection("deliveryzones").Find(filter).ToListAsync(ct))
        {
            var created = DeliveryZone.Create(tenantId, d.Str("name"), Zip(d.Long("zipStart")), Zip(d.Long("zipEnd")), d.Long("fee"), d.Bool("isActive", true));
            if (!Skip(created, $"zona {d.Str("name")}", report))
            {
                Restore.Set(created.Value, "Id", LegacyIds.For("deliveryzone", d.Id()));
                db.DeliveryZones.Add(created.Value);
                report.Count("zonas de entrega");
            }
        }
    }

    private async Task ImportPaymentSettingsAsync(BsonDocument filter, ObjectId legacyTenant, TenantId tenantId, byte[]? nestKey, MigrationReport report, CancellationToken ct)
    {
        var d = await Collection("settings").Find(filter).FirstOrDefaultAsync(ct);
        if (d is null)
        {
            return;
        }

        var settings = PaymentSettings.For(tenantId);
        settings.Update(d.Str("paymentGateway") == "mercadopago", d.Bool("mercadoPagoOfflineFallbackEnabled"), d.NStr("pixKey"), d.NStr("pixReceiverName"), d.NStr("pixCity"));
        if (d.NStr("mercadoPagoAccessToken") is { } stored)
        {
            // Decifra com a chave do Nest (AAD = campo + loja no Nest) e recifra com a chave da API .NET (AAD = loja nova).
            var token = NestSecrets.Decrypt(stored, $"settings.mercadoPagoAccessToken:{legacyTenant}", nestKey);
            if (token is null)
            {
                report.Warnings.Add("Token do Mercado Pago não pôde ser lido (informe --nest-settings-key): a loja precisa recadastrá-lo.");
            }
            else
            {
                settings.SetMercadoPagoToken(secrets.Protect(token, SecretContexts.MercadoPagoToken(tenantId)));
            }
        }

        db.PaymentSettings.Add(settings);
        report.Count("configuração de pagamento");
    }

    // ── Pedidos ──

    private async Task ImportOrdersAsync(BsonDocument filter, TenantId tenantId, Dictionary<ObjectId, Guid> customers, MigrationReport report, CancellationToken ct)
    {
        foreach (var d in await Collection("orders").Find(filter).ToListAsync(ct))
        {
            var items = d.Docs("items").Select(i => new OrderItem(new ProductId(LegacyIds.For("product", i.Str("productId"))), i.NStr("variantId"), i.NStr("variantLabel"),
                i.Str("name"), i.NStr("sku"), i.Long("unitPrice"), i.Long("unitCost"), i.Int("quantity"))).ToList();
            var method = d.Str("deliveryMethod") == "entrega" ? DeliveryMethod.Entrega : DeliveryMethod.Retirada;
            var address = d.Doc("address") is { } a && method == DeliveryMethod.Entrega
                ? new DeliveryAddress(a.Str("recipientName"), a.Str("street"), a.Str("number"), a.Str("complement"), a.Str("neighborhood"), a.Str("city"), a.Str("state"), a.Str("zipCode"), a.Str("deliveryNotes"))
                : null;
            var totals = new OrderTotals(d.Long("subtotal"), d.Long("discount"), d.Long("deliveryFee"), d.Long("giftCardFee"), d.Long("cashbackUsed"), d.Long("tierDiscount"));
            var customerId = d.Oid("customerId") is { } c && customers.TryGetValue(c, out var mapped) ? mapped : (Guid?)null;
            var orderId = new OrderId(LegacyIds.For("order", d.Id()));
            var created = d.Date("createdAt") ?? clock.GetUtcNow();
            var gift = d.Doc("giftCard") is { } g ? new GiftCard(g.Str("message"), g.Str("from"), g.Str("to")) : null;
            var placed = Order.Place(orderId, new OrderPlacement(tenantId, d.NStr("idempotencyKey"),
                new CustomerContact(d.Str("customerName"), d.Str("customerPhone"), d.NStr("customerEmail")?.ToLowerInvariant()), customerId, items, totals, method,
                address, DateOnly.TryParse(d.NStr("deliveryDate"), System.Globalization.CultureInfo.InvariantCulture, out var day) ? day : null,
                Window(d.NStr("deliveryTimeWindow")), null, null, gift, null, d.NStr("couponCode"), Substitution(d.Str("substitutionMode"))), created);
            if (Skip(placed, $"pedido {d.Id()}", report))
            {
                report.RejectedOrders.Add(d.Id());
                continue;
            }

            var order = placed.Value.Order;
            var status = Status(d.Str("status"));
            Restore.Set(order, nameof(Order.Status), status);
            if (d.Long("total") != order.TotalCents)
            {
                report.Warnings.Add($"Pedido {d.Id()}: total recalculado {order.TotalCents} ≠ {d.Long("total")} do Nest; mantido o do Nest.");
                Restore.Set(order, nameof(Order.TotalCents), d.Long("total"));
            }

            var history = Restore.Get<List<StatusChange>>(order, "_history");
            history.Clear();
            history.Add(new StatusChange(null, OrderStatus.Pendente, created, null, "customer", "Pedido criado (sistema anterior)", null));
            if (status != OrderStatus.Pendente)
            {
                history.Add(new StatusChange(OrderStatus.Pendente, status, d.Date("updatedAt") ?? created, null, "system", "Estado migrado do sistema anterior", null));
            }

            // O link de acompanhamento que o cliente já tem continua valendo: o Nest guardava o token em claro; aqui fica o hash.
            if (d.NStr("accessToken") is { } token)
            {
                Restore.Set(order, nameof(Order.AccessTokenHash), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
            }

            Restore.Set(order, nameof(Order.PaymentStatus), d.Str("paymentStatus", "pending"));
            Restore.Set(order, nameof(Order.PaymentInitializationStatus), d.Str("paymentInitializationStatus", "ready"));
            Restore.Set(order, nameof(Order.PaymentRail), d.NStr("paymentRail"));
            Restore.Set(order, nameof(Order.PixCode), d.Str("pixCode"));
            Restore.Set(order, nameof(Order.Origin), d.Str("origin", "online"));
            Restore.Set(order, nameof(Order.PaymentMethod), d.Str("paymentMethod", "pix"));
            Restore.Set(order, nameof(Order.RefundStatus), d.Str("refundStatus", "not_required"));
            Restore.Set(order, nameof(Order.RefundedAmountCents), d.Long("refundedAmount"));
            Restore.Set(order, nameof(Order.UpdatedAt), d.Date("updatedAt") ?? created);
            db.Orders.Add(order);
            report.Count("pedidos");
            ImportPaymentIntent(d, order, report);
        }
    }

    /// <summary>Pedido PAGO ganha a intenção de pagamento: reembolsar um pedido migrado continua possível (ADR 0008).</summary>
    private void ImportPaymentIntent(BsonDocument d, Order order, MigrationReport report)
    {
        if (d.Str("paymentStatus") is not ("approved" or "refunded"))
        {
            return;
        }

        var rail = d.NStr("gatewayPaymentId") is not null ? PaymentRail.MercadoPago : PaymentRail.PixOffline;
        var paidAt = d.Date("updatedAt") ?? order.CreatedAt;
        var intent = PaymentIntent.Open(order.TenantId, order.Id, order.TotalCents, rail, order.PixCode.Length > 0 ? order.PixCode : "migrado", d.NStr("gatewayPaymentId"), false, order.CreatedAt);
        intent.RecordProviderStatus(ProviderPaymentStatus.Approved, order.TotalCents, $"import:{d.Id()}", paidAt);
        if (d.Long("refundedAmount") is > 0 and var refunded)
        {
            intent.ReserveRefund(Math.Min(refunded, order.TotalCents));
            intent.CompleteRefund(Math.Min(refunded, order.TotalCents));
        }

        intent.ClearDomainEvents();
        db.PaymentIntents.Add(intent);
        report.Count("intenções de pagamento");
    }

    // ── Conferência ──

    private async Task ReconcileAsync(BsonDocument filter, MigrationReport report, CancellationToken ct)
    {
        // Origem = o que está no Mongo MENOS o que foi recusado (com --force); destino = o que ficou no PostgreSQL.
        var products = (await Collection("products").Find(filter).ToListAsync(ct)).Where(p => !report.RejectedProducts.Contains(p.Id())).ToList();
        var orders = (await Collection("orders").Find(filter).ToListAsync(ct)).Where(o => !report.RejectedOrders.Contains(o.Id())).ToList();
        var rejectedPeople = report.Rejected.Count(r => r.StartsWith("cliente", StringComparison.Ordinal));
        var rejectedStaff = report.Rejected.Count(r => r.StartsWith("usuário", StringComparison.Ordinal));

        report.Reconciliation.Add(("produtos", products.Count, await db.Products.CountAsync(ct)));
        report.Reconciliation.Add(("pedidos", orders.Count, await db.Orders.CountAsync(ct)));
        report.Reconciliation.Add(("clientes", await Collection("customers").CountDocumentsAsync(filter, cancellationToken: ct) - rejectedPeople, await db.Customers.CountAsync(ct)));
        report.Reconciliation.Add(("equipe", await Collection("adminusers").CountDocumentsAsync(filter, cancellationToken: ct) - rejectedStaff, await db.StaffUsers.CountAsync(ct)));

        // Unidades em estoque: a soma de product.stock no Nest = soma do saldo dos lotes aqui.
        report.Reconciliation.Add(("unidades em estoque", products.Sum(p => (long)Math.Max(0, p.Int("stock"))), await db.StockItems.SumAsync(s => (long)s.OnHand, ct)));

        // Faturamento: a soma dos totais dos pedidos tem que ser a mesma, centavo a centavo.
        report.Reconciliation.Add(("soma dos pedidos (centavos)", orders.Sum(o => o.Long("total")), await db.Orders.SumAsync(o => o.TotalCents, ct)));
    }

    // ── Apoio ──

    private IMongoCollection<BsonDocument> Collection(string name) => mongo.GetCollection<BsonDocument>(name);

    /// <summary>
    /// Filtro da loja + condição extra, numa CÓPIA. <c>BsonDocument.Merge</c> altera o documento base: usado
    /// aqui, o filtro de lotes (<c>quantity &gt; 0</c>) grudou no filtro da loja e a busca de produtos voltou
    /// vazia. Quem pegou foi a conferência no fim (que recusou gravar a migração pela metade).
    /// </summary>
    private static BsonDocument With(BsonDocument tenantFilter, BsonDocument extra)
    {
        var copy = new BsonDocument(tenantFilter);
        foreach (var element in extra)
        {
            copy[element.Name] = element.Value;
        }

        return copy;
    }

    private static bool Skip<T>(Result<T> result, string what, MigrationReport report)
    {
        if (result.IsSuccess)
        {
            return false;
        }

        report.Rejected.Add($"{what}: {result.Error.Message}");
        return true;
    }

    private void ClearDomainEvents()
    {
        foreach (var entry in db.ChangeTracker.Entries<IAggregateRoot>())
        {
            entry.Entity.ClearDomainEvents();
        }
    }

    /// <summary>
    /// Re-ensaio (<c>--replace</c>): apaga a loja inteira do PostgreSQL. A lista de tabelas é DESCOBERTA (toda
    /// tabela com <c>tenant_id</c>), não escrita à mão: módulo novo não fica esquecido. A ordem das chaves
    /// estrangeiras se resolve sozinha, em passadas: o que falha por FK volta na passada seguinte (savepoint).
    /// </summary>
    private async Task DeleteTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.columns WHERE column_name = 'tenant_id' AND table_schema = 'public'").ToListAsync(ct);
        for (var pass = 0; tables.Count > 0 && pass < 20; pass++)
        {
            var failed = new List<string>();
            foreach (var table in tables)
            {
                // Nome de tabela não pode ser parâmetro SQL: vem do information_schema E é validado como identificador simples.
                if (!System.Text.RegularExpressions.Regex.IsMatch(table, "^[a-z_][a-z0-9_]*$"))
                {
                    throw new InvalidOperationException($"Nome de tabela inesperado: {table}");
                }

                await db.Database.ExecuteSqlRawAsync("SAVEPOINT replace_tenant", ct);
                try
                {
#pragma warning disable EF1002 // identificador validado acima; o valor (tenant) vai como parâmetro
                    await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\" WHERE tenant_id = {{0}}", [tenantId.Value], ct);
#pragma warning restore EF1002
                    await db.Database.ExecuteSqlRawAsync("RELEASE SAVEPOINT replace_tenant", ct);
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.ForeignKeyViolation)
                {
                    await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT replace_tenant", ct);
                    failed.Add(table);
                }
            }

            tables = failed;
        }

        await db.Database.ExecuteSqlAsync($"DELETE FROM tenants WHERE id = {tenantId.Value}", ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Variação com id fora do formato atual (<c>var_</c> + 24 hex; dado antigo do Nest) ganha um id NOVO e
    /// DETERMINÍSTICO (hash do id antigo): o produto não é recusado e o ensaio seguinte gera o mesmo id.
    /// </summary>
    private static string? VariantIdFor(BsonDocument product, BsonDocument variant, MigrationReport report)
    {
        var legacy = variant.NStr("id");
        if (legacy is null || VariantId.IsValid(legacy))
        {
            return legacy;
        }

        var normalized = $"var_{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{product.Id()}:{legacy}")))[..24]}";
        report.Warnings.Add($"{product.Str("name")}: variação \"{variant.Str("label")}\" tinha id fora do padrão ({legacy}) e passou a {normalized}.");
        return normalized;
    }

    private static IEnumerable<string> CategoryIds(BsonDocument d) =>
        d.TryGetValue("categoryIds", out var v) && v.IsBsonArray ? v.AsBsonArray.Where(x => x.IsObjectId).Select(x => x.AsObjectId.ToString()) : [];

    private static ProductCategory ParseCategory(string value) => value switch
    {
        "romance" => ProductCategory.Romance,
        "eventos" => ProductCategory.Eventos,
        "presentes" => ProductCategory.Presentes,
        "cartoes" => ProductCategory.Cartoes,
        _ => ProductCategory.Buques,
    };

    private static OrderStatus Status(string value) => OrderStatusContract.Parse(value) ?? OrderStatus.Pendente;

    private static DeliveryWindow? Window(string? value) => value switch
    {
        "manha" => DeliveryWindow.Manha,
        "tarde" => DeliveryWindow.Tarde,
        "noite" => DeliveryWindow.Noite,
        _ => null,
    };

    private static SubstitutionMode Substitution(string value) => value switch
    {
        "exact" => SubstitutionMode.Exact,
        "preapproved" => SubstitutionMode.Preapproved,
        _ => SubstitutionMode.AskMe,
    };

    private static string Zip(long value) => value.ToString("00000000", System.Globalization.CultureInfo.InvariantCulture);
}
