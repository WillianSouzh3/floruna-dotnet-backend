using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Floruna.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Refunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "refund_reserved_cents",
                table: "payment_intents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "refunded_amount_cents",
                table: "payment_intents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "refund_status",
                table: "orders",
                type: "text",
                nullable: false,
                defaultValue: "not_required"); // pedidos existentes: nenhum reembolso pendente

            migrationBuilder.AddColumn<long>(
                name: "refunded_amount_cents",
                table: "orders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_cents = table.Column<long>(type: "bigint", nullable: false),
                    scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    restock = table.Column<bool>(type: "boolean", nullable: false),
                    requested_by = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_refund_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    items = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amount_positive", "amount_cents > 0");
                    table.ForeignKey(
                        name: "fk_refunds_payment_intents_intent_id",
                        column: x => x.intent_id,
                        principalTable: "payment_intents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payment_intents_refund_within_paid",
                table: "payment_intents",
                sql: "refunded_amount_cents >= 0 AND refund_reserved_cents >= 0 AND refunded_amount_cents + refund_reserved_cents <= paid_amount_cents");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_intent_id",
                table: "refunds",
                column: "intent_id");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_pending_due",
                table: "refunds",
                column: "next_attempt_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_tenant_id_idempotency_key",
                table: "refunds",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refunds_tenant_id_intent_id",
                table: "refunds",
                columns: new[] { "tenant_id", "intent_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payment_intents_refund_within_paid",
                table: "payment_intents");

            migrationBuilder.DropColumn(
                name: "refund_reserved_cents",
                table: "payment_intents");

            migrationBuilder.DropColumn(
                name: "refunded_amount_cents",
                table: "payment_intents");

            migrationBuilder.DropColumn(
                name: "refund_status",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "refunded_amount_cents",
                table: "orders");
        }
    }
}
