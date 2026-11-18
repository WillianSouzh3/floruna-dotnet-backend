using Floruna.Api.IntegrationTests.Support;
using Floruna.Application.Abstractions;
using Floruna.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Floruna.Api.IntegrationTests;

/// <summary>
/// Migration com DADOS é onde se perde informação em produção. Este teste sobe um
/// banco NOVO até a migration anterior, grava produtos com o estoque no formato
/// antigo (<c>products.stock</c>), aplica a migration de estoque e confere que
/// nenhum saldo sumiu.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InventoryMigrationTests(FlorunaApiFactory factory)
{
    [Fact]
    public async Task Inventory_migration_turns_old_product_stock_into_lots_and_ledger()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await using var db = new FlorunaDbContext(
            new DbContextOptionsBuilder<FlorunaDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options,
            new TenantContext());
        var migrator = db.GetService<IMigrator>();
        var migrations = db.Database.GetMigrations().ToList();
        var inventory = migrations.Single(m => m.EndsWith("_Inventory", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(inventory) - 1];

        await migrator.MigrateAsync(previous);
        var tenant = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO tenants (id, name, slug, status, plan, custom_domain_verified, referral_code, currency, time_zone, locale, created_at)
            VALUES ({tenant}, 'Loja Antiga', 'loja-antiga', 'Active', 'Free', false, 'FLORA-OLD00001', 'BRL', 'America/Sao_Paulo', 'pt-BR', now());
            INSERT INTO products (id, tenant_id, sku, barcode, slug, name, short_description, description, category, price_cents,
                                  images, stock, low_stock_threshold, is_active, is_featured, created_at, updated_at, cost_cents)
            VALUES (gen_random_uuid(), {tenant}, '', '', 'com-estoque', 'Com estoque', 'curta descr', 'descrição longa', 'Buques', 1000,
                    ARRAY['x'], 7, 3, true, false, now(), now(), 250),
                   (gen_random_uuid(), {tenant}, '', '', 'zerado', 'Zerado', 'curta descr', 'descrição longa', 'Buques', 1000,
                    ARRAY['x'], 0, 3, true, false, now(), now(), NULL);
            """);

        await migrator.MigrateAsync(inventory);

        var items = await Scalar($"SELECT count(*) FROM stock_items WHERE tenant_id = '{tenant}'");
        var onHand = await Scalar($"SELECT sum(on_hand) FROM stock_items WHERE tenant_id = '{tenant}'");
        var lots = await Scalar($"SELECT count(*) FROM stock_lots WHERE tenant_id = '{tenant}' AND remaining = 7 AND unit_cost_cents = 250");
        var ledger = await Scalar($"SELECT count(*) FROM stock_movements WHERE tenant_id = '{tenant}' AND type = 'OpeningBalance' AND delta = 7");

        items.ShouldBe(2);   // todo produto ganhou estoque, inclusive o zerado
        onHand.ShouldBe(7);  // nenhum saldo sumiu
        lots.ShouldBe(1);    // saldo positivo virou lote, com o custo
        ledger.ShouldBe(1);  // e foi explicado no ledger

        async Task<long> Scalar(string sql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = $"migration_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(factory.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(factory.ConnectionString) { Database = name }.ConnectionString;
    }
}
