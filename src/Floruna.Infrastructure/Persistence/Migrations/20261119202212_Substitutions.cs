using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Floruna.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Substitutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "substitutions",
                table: "orders",
                type: "jsonb",
                nullable: true,
                defaultValueSql: "'[]'::jsonb"); // pedidos existentes: nenhuma troca

            migrationBuilder.CreateTable(
                name: "substitution_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    default_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    preapproved_tolerance_percent = table.Column<int>(type: "integer", nullable: false),
                    consent_window_hours = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_substitution_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_substitution_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "substitution_proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_index = table.Column<int>(type: "integer", nullable: false),
                    original_product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    substitute_product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    substitute_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    value_delta = table.Column<long>(type: "bigint", nullable: false),
                    value_delta_percent = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    order_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    consent_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    restock_original = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_substitution_proposals", x => x.id);
                    table.CheckConstraint("ck_substitution_proposals_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_substitution_proposals_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_substitution_policies_tenant_id",
                table: "substitution_policies",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_substitution_proposals_pending_expiry",
                table: "substitution_proposals",
                column: "expires_at",
                filter: "state = 'Proposed'");

            migrationBuilder.CreateIndex(
                name: "ix_substitution_proposals_tenant_id_idempotency_key",
                table: "substitution_proposals",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_substitution_proposals_tenant_id_order_id_created_at",
                table: "substitution_proposals",
                columns: new[] { "tenant_id", "order_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "substitution_policies");

            migrationBuilder.DropTable(
                name: "substitution_proposals");

            migrationBuilder.DropColumn(
                name: "substitutions",
                table: "orders");
        }
    }
}
