using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Floruna.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "custom_order_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    customer_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    customer_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    occasion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    container_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    size = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    color_palette = table.Column<List<string>>(type: "text[]", nullable: false),
                    flower_preferences = table.Column<List<string>>(type: "text[]", nullable: false),
                    ribbon = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    card_message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    budget_range = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    desired_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    quoted_price = table.Column<long>(type: "bigint", nullable: true),
                    admin_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_custom_order_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_custom_order_requests_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "custom_quotes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    response_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    converted_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lines = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_custom_quotes", x => x.id);
                    table.CheckConstraint("ck_custom_quotes_total_not_negative", "total >= 0");
                    table.ForeignKey(
                        name: "fk_custom_quotes_custom_order_requests_request_id",
                        column: x => x.request_id,
                        principalTable: "custom_order_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_custom_order_requests_tenant_id_status_created_at",
                table: "custom_order_requests",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_custom_quotes_published_valid_until",
                table: "custom_quotes",
                column: "valid_until",
                filter: "state = 'Published'");

            migrationBuilder.CreateIndex(
                name: "ix_custom_quotes_request_id",
                table: "custom_quotes",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "ix_custom_quotes_tenant_id_converted_order_id",
                table: "custom_quotes",
                columns: new[] { "tenant_id", "converted_order_id" },
                unique: true,
                filter: "converted_order_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_custom_quotes_tenant_id_request_id_version",
                table: "custom_quotes",
                columns: new[] { "tenant_id", "request_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_quotes");

            migrationBuilder.DropTable(
                name: "custom_order_requests");
        }
    }
}
