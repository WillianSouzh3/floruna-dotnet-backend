using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Floruna.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PosAndCash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "cash_received_cents",
                table: "orders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "cash_session_id",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "operator_id",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "orders",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "online"); // todo pedido anterior veio da vitrine

            migrationBuilder.AddColumn<string>(
                name: "payment_method",
                table: "orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "pix");

            migrationBuilder.AddColumn<string>(
                name: "payments",
                table: "orders",
                type: "jsonb",
                nullable: true,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.CreateTable(
                name: "cash_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    opening_float_cents = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    counted_cash = table.Column<long>(type: "bigint", nullable: true),
                    expected_cash_at_close = table.Column<long>(type: "bigint", nullable: true),
                    difference = table.Column<long>(type: "bigint", nullable: true),
                    movements = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_sessions", x => x.id);
                    table.CheckConstraint("ck_cash_sessions_float_not_negative", "opening_float_cents >= 0");
                    table.ForeignKey(
                        name: "fk_cash_sessions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_cash_session_id",
                table: "orders",
                column: "cash_session_id",
                filter: "cash_session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cash_sessions_tenant_id_status_closed_at",
                table: "cash_sessions",
                columns: new[] { "tenant_id", "status", "closed_at" });

            migrationBuilder.CreateIndex(
                name: "ux_cash_sessions_one_open_per_operator",
                table: "cash_sessions",
                columns: new[] { "tenant_id", "operator_id" },
                unique: true,
                filter: "status = 'Open'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_sessions");

            migrationBuilder.DropIndex(
                name: "ix_orders_cash_session_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "cash_received_cents",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "cash_session_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "operator_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payment_method",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payments",
                table: "orders");
        }
    }
}
