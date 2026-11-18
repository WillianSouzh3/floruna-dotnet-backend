using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Floruna.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CheckoutOrdersOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coupons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: false),
                    min_order_cents = table.Column<long>(type: "bigint", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    per_customer_limit = table.Column<int>(type: "integer", nullable: true),
                    used_count = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    target_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coupons", x => x.id);
                    table.CheckConstraint("ck_coupons_used_within_limit", "max_uses IS NULL OR used_count <= max_uses");
                    table.ForeignKey(
                        name: "fk_coupons_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_capacity_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    weekday = table.Column<int>(type: "integer", nullable: true),
                    date = table.Column<DateOnly>(type: "date", nullable: true),
                    window = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    zone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    cutoff_minutes = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_capacity_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_delivery_capacity_rules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_slot_usage",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    window = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    zone_key = table.Column<Guid>(type: "uuid", nullable: false),
                    used = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_slot_usage", x => new { x.tenant_id, x.date, x.window, x.zone_key });
                    table.CheckConstraint("ck_delivery_slot_usage_used", "used >= 0");
                });

            migrationBuilder.CreateTable(
                name: "delivery_zones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    zip_start = table.Column<int>(type: "integer", nullable: false),
                    zip_end = table.Column<int>(type: "integer", nullable: false),
                    fee_cents = table.Column<long>(type: "bigint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_zones", x => x.id);
                    table.CheckConstraint("ck_delivery_zones_range", "zip_start <= zip_end");
                    table.ForeignKey(
                        name: "fk_delivery_zones_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subtotal_cents = table.Column<long>(type: "bigint", nullable: false),
                    discount_cents = table.Column<long>(type: "bigint", nullable: false),
                    delivery_fee_cents = table.Column<long>(type: "bigint", nullable: false),
                    gift_card_fee_cents = table.Column<long>(type: "bigint", nullable: false),
                    total_cents = table.Column<long>(type: "bigint", nullable: false),
                    delivery_method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    delivery_window = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    delivery_zone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delivery_slot_scope = table.Column<Guid>(type: "uuid", nullable: true),
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: true),
                    coupon_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    substitution_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    access_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payment_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    address = table.Column<string>(type: "jsonb", nullable: true),
                    customer = table.Column<string>(type: "jsonb", nullable: false),
                    gift_card = table.Column<string>(type: "jsonb", nullable: true),
                    items = table.Column<string>(type: "jsonb", nullable: true),
                    status_history = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_discount_within_items", "discount_cents <= subtotal_cents");
                    table.CheckConstraint("ck_orders_total_positive", "total_cents > 0");
                    table.ForeignKey(
                        name: "fk_orders_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_receipts",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handler = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_receipts", x => new { x.message_id, x.handler });
                });

            migrationBuilder.CreateTable(
                name: "coupon_redemptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    coupon_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discount_cents = table.Column<long>(type: "bigint", nullable: false),
                    order_total_cents = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_coupon_redemptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_coupon_redemptions_coupons_coupon_id",
                        column: x => x.coupon_id,
                        principalTable: "coupons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_coupon_redemptions_coupon_id",
                table: "coupon_redemptions",
                column: "coupon_id");

            migrationBuilder.CreateIndex(
                name: "ix_coupon_redemptions_tenant_id_coupon_id_email",
                table: "coupon_redemptions",
                columns: new[] { "tenant_id", "coupon_id", "email" });

            migrationBuilder.CreateIndex(
                name: "ix_coupon_redemptions_tenant_id_order_id",
                table: "coupon_redemptions",
                columns: new[] { "tenant_id", "order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_coupons_tenant_id_code",
                table: "coupons",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_delivery_capacity_rules_tenant_id_kind_date_weekday",
                table: "delivery_capacity_rules",
                columns: new[] { "tenant_id", "kind", "date", "weekday" });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_zones_tenant_id_is_active_zip_start_zip_end",
                table: "delivery_zones",
                columns: new[] { "tenant_id", "is_active", "zip_start", "zip_end" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_pending_payment_deadline",
                table: "orders",
                column: "payment_deadline",
                filter: "status = 'Pendente'");

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_customer_id_created_at",
                table: "orders",
                columns: new[] { "tenant_id", "customer_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_idempotency_key",
                table: "orders",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_status_created_at",
                table: "orders",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_state_next_attempt_at",
                table: "outbox_messages",
                columns: new[] { "state", "next_attempt_at" },
                filter: "state IN ('Pending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_tenant_id_occurred_at",
                table: "outbox_messages",
                columns: new[] { "tenant_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coupon_redemptions");

            migrationBuilder.DropTable(
                name: "delivery_capacity_rules");

            migrationBuilder.DropTable(
                name: "delivery_slot_usage");

            migrationBuilder.DropTable(
                name: "delivery_zones");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_receipts");

            migrationBuilder.DropTable(
                name: "coupons");
        }
    }
}
