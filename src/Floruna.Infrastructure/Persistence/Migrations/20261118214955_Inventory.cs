using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Floruna.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Inventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stock_items",
                columns: table => new
                {
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_hand = table.Column<int>(type: "integer", nullable: false),
                    reserved = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_items", x => x.product_id);
                    table.CheckConstraint("ck_stock_items_on_hand_non_negative", "on_hand >= 0");
                    table.CheckConstraint("ck_stock_items_reserved_valid", "reserved >= 0 AND reserved <= on_hand");
                    table.ForeignKey(
                        name: "fk_stock_items_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    delta = table.Column<int>(type: "integer", nullable: false),
                    resulting_stock = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stock_lots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    initial_quantity = table.Column<int>(type: "integer", nullable: false),
                    remaining = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unit_cost_cents = table.Column<long>(type: "bigint", nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    received_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_lots", x => x.id);
                    table.CheckConstraint("ck_stock_lots_remaining_valid", "remaining >= 0 AND remaining <= initial_quantity");
                    table.ForeignKey(
                        name: "fk_stock_lots_stock_items_product_id",
                        column: x => x.product_id,
                        principalTable: "stock_items",
                        principalColumn: "product_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    owner_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    owner_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_reservations", x => x.id);
                    table.CheckConstraint("ck_stock_reservations_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_stock_reservations_stock_items_product_id",
                        column: x => x.product_id,
                        principalTable: "stock_items",
                        principalColumn: "product_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_movement_lots",
                columns: table => new
                {
                    lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_cost_cents = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movement_lots", x => new { x.movement_id, x.lot_id });
                    table.ForeignKey(
                        name: "fk_stock_movement_lots_stock_movements_movement_id",
                        column: x => x.movement_id,
                        principalTable: "stock_movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_lots_product_id",
                table: "stock_lots",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_lots_tenant_id_expires_at",
                table: "stock_lots",
                columns: new[] { "tenant_id", "expires_at" },
                filter: "remaining > 0 AND expires_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stock_lots_tenant_id_product_id_expires_at",
                table: "stock_lots",
                columns: new[] { "tenant_id", "product_id", "expires_at" },
                filter: "remaining > 0");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_tenant_id_idempotency_key",
                table: "stock_movements",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stock_movements_tenant_id_product_id_created_at",
                table: "stock_movements",
                columns: new[] { "tenant_id", "product_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_held_expires_at",
                table: "stock_reservations",
                column: "expires_at",
                filter: "state = 'Held'");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_product_id",
                table: "stock_reservations",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_tenant_id_idempotency_key",
                table: "stock_reservations",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_reservations_tenant_id_owner_type_owner_id",
                table: "stock_reservations",
                columns: new[] { "tenant_id", "owner_type", "owner_id" });

            // ── Backfill: o saldo que estava em products.stock vira estoque de verdade ──
            // Cada produto ganha seu stock_item; o saldo positivo vira UM lote sem validade
            // e UM movimento opening_balance no ledger (nada aparece sem explicação).
            // Só DEPOIS a coluna antiga é removida (a ordem gerada pelo EF apagaria os saldos).
            migrationBuilder.Sql("""
                INSERT INTO stock_items (product_id, tenant_id, on_hand, reserved)
                SELECT id, tenant_id, stock, 0 FROM products;

                INSERT INTO stock_lots (id, tenant_id, product_id, initial_quantity, remaining, expires_at, unit_cost_cents, note, received_by, received_at)
                SELECT gen_random_uuid(), tenant_id, id, stock, stock, NULL, cost_cents, 'Saldo migrado', 'migracao', now()
                FROM products WHERE stock > 0;

                INSERT INTO stock_movements (id, tenant_id, product_id, product_name, type, delta, resulting_stock, note, by, order_id, idempotency_key, created_at)
                SELECT gen_random_uuid(), tenant_id, id, name, 'OpeningBalance', stock, stock, 'Saldo migrado', 'migracao', NULL,
                       'opening-balance:' || id::text, now()
                FROM products WHERE stock > 0;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_stock_non_negative",
                table: "products");

            migrationBuilder.DropColumn(
                name: "stock",
                table: "products");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_lots");

            migrationBuilder.DropTable(
                name: "stock_movement_lots");

            migrationBuilder.DropTable(
                name: "stock_reservations");

            migrationBuilder.DropTable(
                name: "stock_movements");

            migrationBuilder.DropTable(
                name: "stock_items");

            migrationBuilder.AddColumn<int>(
                name: "stock",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_stock_non_negative",
                table: "products",
                sql: "stock >= 0");
        }
    }
}
