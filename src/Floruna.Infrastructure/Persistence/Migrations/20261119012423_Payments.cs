using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Floruna.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Payments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "payment_initialization_status",
                table: "orders",
                type: "text",
                nullable: false,
                defaultValue: "pending");

            migrationBuilder.AddColumn<string>(
                name: "payment_rail",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_status",
                table: "orders",
                type: "text",
                nullable: false,
                defaultValue: "pending");

            migrationBuilder.AddColumn<string>(
                name: "pix_code",
                table: "orders",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "payment_intents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    rail = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    pix_code = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    provider_payment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    fallback_used = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    paid_amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reconciliation_issue = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    transactions = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_intents", x => x.id);
                    table.CheckConstraint("ck_payment_intents_amount_positive", "amount_cents > 0");
                    table.ForeignKey(
                        name: "fk_payment_intents_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_payment_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    use_mercado_pago = table.Column<bool>(type: "boolean", nullable: false),
                    offline_fallback_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    pix_key = table.Column<string>(type: "character varying(77)", maxLength: 77, nullable: false),
                    pix_receiver_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    pix_city = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    mercado_pago_token_ciphertext = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payment_intents_order_id",
                table: "payment_intents",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_payment_intents_reconciliation_issue",
                table: "payment_intents",
                column: "reconciliation_issue",
                filter: "reconciliation_issue IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_intents_tenant_id_order_id",
                table: "payment_intents",
                columns: new[] { "tenant_id", "order_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_intents_tenant_id_provider_payment_id",
                table: "payment_intents",
                columns: new[] { "tenant_id", "provider_payment_id" },
                filter: "provider_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payment_notifications_tenant_id_provider_payment_id_receive",
                table: "payment_notifications",
                columns: new[] { "tenant_id", "provider_payment_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_settings_tenant_id",
                table: "payment_settings",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_intents");

            migrationBuilder.DropTable(
                name: "payment_notifications");

            migrationBuilder.DropTable(
                name: "payment_settings");

            migrationBuilder.DropColumn(
                name: "payment_initialization_status",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payment_rail",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payment_status",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "pix_code",
                table: "orders");
        }
    }
}
