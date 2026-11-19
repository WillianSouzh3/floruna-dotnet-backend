using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Floruna.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Loyalty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "cashback_used_cents",
                table: "orders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "loyalty_account_id",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "loyalty_accrued_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "loyalty_reversed_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "tier_discount_cents",
                table: "orders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "loyalty_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    cashback_balance = table.Column<long>(type: "bigint", nullable: false),
                    total_spent_cents = table.Column<long>(type: "bigint", nullable: false),
                    orders_count = table.Column<int>(type: "integer", nullable: false),
                    flower_kinds = table.Column<List<string>>(type: "text[]", nullable: false),
                    tier_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loyalty_accounts", x => x.id);
                    table.CheckConstraint("ck_loyalty_accounts_balance", "cashback_balance >= 0");
                    table.ForeignKey(
                        name: "fk_loyalty_accounts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_programs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    cashback_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    cashback_min_redemption = table.Column<long>(type: "bigint", nullable: false),
                    cashback_expiry_days = table.Column<int>(type: "integer", nullable: false),
                    stacking = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    max_total_discount_percent = table.Column<int>(type: "integer", nullable: false),
                    max_cashback_percent_of_order = table.Column<int>(type: "integer", nullable: false),
                    allow_cashback_with_coupon = table.Column<bool>(type: "boolean", nullable: false),
                    tiers = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loyalty_programs", x => x.id);
                    table.CheckConstraint("ck_loyalty_programs_cashback_share", "max_cashback_percent_of_order BETWEEN 0 AND 90");
                    table.ForeignKey(
                        name: "fk_loyalty_programs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    remaining = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_loyalty_entries", x => x.id);
                    table.CheckConstraint("ck_loyalty_entries_amount", "amount <> 0");
                    table.CheckConstraint("ck_loyalty_entries_remaining", "remaining >= 0 AND remaining <= GREATEST(amount, 0)");
                    table.ForeignKey(
                        name: "fk_loyalty_entries_loyalty_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "loyalty_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_loyalty_entries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_accounts_tenant_id_email",
                table: "loyalty_accounts",
                columns: new[] { "tenant_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_accounts_tenant_id_total_spent_cents",
                table: "loyalty_accounts",
                columns: new[] { "tenant_id", "total_spent_cents" });

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_entries_account_id_created_at",
                table: "loyalty_entries",
                columns: new[] { "account_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_entries_expiring",
                table: "loyalty_entries",
                column: "expires_at",
                filter: "remaining > 0");

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_entries_open_lots",
                table: "loyalty_entries",
                columns: new[] { "account_id", "expires_at" },
                filter: "remaining > 0");

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_entries_tenant_id_dedupe_key",
                table: "loyalty_entries",
                columns: new[] { "tenant_id", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_programs_tenant_id",
                table: "loyalty_programs",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loyalty_entries");

            migrationBuilder.DropTable(
                name: "loyalty_programs");

            migrationBuilder.DropTable(
                name: "loyalty_accounts");

            migrationBuilder.DropColumn(
                name: "cashback_used_cents",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "loyalty_account_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "loyalty_accrued_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "loyalty_reversed_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "tier_discount_cents",
                table: "orders");
        }
    }
}
