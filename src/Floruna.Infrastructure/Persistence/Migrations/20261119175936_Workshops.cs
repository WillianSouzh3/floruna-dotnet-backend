using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Floruna.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Workshops : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workshops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    short_description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    date = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    image = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    price_cents = table.Column<long>(type: "bigint", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    registered = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workshops", x => x.id);
                    table.CheckConstraint("ck_workshops_price_not_negative", "price_cents >= 0");
                    table.CheckConstraint("ck_workshops_registered_within_capacity", "registered >= 0 AND registered <= capacity");
                    table.ForeignKey(
                        name: "fk_workshops_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workshop_registrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workshop_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workshop_registrations", x => x.id);
                    table.ForeignKey(
                        name: "fk_workshop_registrations_workshops_workshop_id",
                        column: x => x.workshop_id,
                        principalTable: "workshops",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workshop_registrations_tenant_id_workshop_id_created_at",
                table: "workshop_registrations",
                columns: new[] { "tenant_id", "workshop_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workshop_registrations_workshop_id",
                table: "workshop_registrations",
                column: "workshop_id");

            migrationBuilder.CreateIndex(
                name: "ix_workshops_tenant_id_is_active_created_at",
                table: "workshops",
                columns: new[] { "tenant_id", "is_active", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workshops_tenant_id_slug",
                table: "workshops",
                columns: new[] { "tenant_id", "slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workshop_registrations");

            migrationBuilder.DropTable(
                name: "workshops");
        }
    }
}
