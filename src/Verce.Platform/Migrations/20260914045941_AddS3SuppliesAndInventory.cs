using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddS3SuppliesAndInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateSequence(
                name: "supply_creation_sequence_seq",
                schema: "inventory");

            migrationBuilder.CreateTable(
                name: "supply_category",
                schema: "inventory",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supply_category", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "supply",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    category_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    base_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    minimum_stock = table.Column<decimal>(type: "numeric(14,4)", nullable: true),
                    preferred_supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    current_stock_base_unit = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    latest_purchase_unit_cost = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    has_recorded_movement = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    creation_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('inventory.supply_creation_sequence_seq')"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    filament_brand = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    filament_color_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    filament_color_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    filament_diameter_mm = table.Column<decimal>(type: "numeric(6,4)", nullable: true),
                    filament_material_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    filament_spool_net_weight_grams = table.Column<decimal>(type: "numeric(12,3)", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_supply", x => x.id);
                    table.CheckConstraint("ck_supply_base_unit", "base_unit IN ('Gram', 'Kilogram', 'Unit', 'Milliliter', 'Liter', 'Meter', 'Centimeter')");
                    table.CheckConstraint("ck_supply_filament_material_type", "filament_material_type IS NULL OR filament_material_type IN ('Pla', 'PlaPlus', 'Petg', 'Abs', 'Asa', 'Tpu', 'Nylon', 'Pc', 'Pva', 'Other')");
                    table.ForeignKey(
                        name: "fk_supply_supply_category_category_code",
                        column: x => x.category_code,
                        principalSchema: "inventory",
                        principalTable: "supply_category",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            // ADR-0011 §1.2.1: the sequence is OWNED BY its column so PostgreSQL drops it
            // automatically alongside the table (see the matching note in Down below) — no
            // separate DropSequence is issued for it.
            migrationBuilder.Sql(
                "ALTER SEQUENCE inventory.supply_creation_sequence_seq OWNED BY inventory.supply.creation_sequence;");

            migrationBuilder.CreateTable(
                name: "inventory_movement",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    supply_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    entered_quantity = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    entered_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    quantity_delta_base_unit = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    total_cost_snapshot = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inventory_movement", x => x.id);
                    table.CheckConstraint("ck_inventory_movement_entered_unit", "entered_unit IN ('Gram', 'Kilogram', 'Unit', 'Milliliter', 'Liter', 'Meter', 'Centimeter')");
                    table.CheckConstraint("ck_inventory_movement_type", "type IN ('PurchaseReceipt', 'ManualIncrease', 'ManualDecrease', 'Consumption', 'ReturnIn', 'ReturnOut', 'InitialBalance', 'Correction')");
                    table.ForeignKey(
                        name: "fk_inventory_movement_supply_supply_id",
                        column: x => x.supply_id,
                        principalSchema: "inventory",
                        principalTable: "supply",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_inventory_movement_supply_id_occurred_at",
                schema: "inventory",
                table: "inventory_movement",
                columns: new[] { "supply_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_supply_active",
                schema: "inventory",
                table: "supply",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_supply_category_code",
                schema: "inventory",
                table: "supply",
                column: "category_code");

            migrationBuilder.CreateIndex(
                name: "ix_supply_code",
                schema: "inventory",
                table: "supply",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supply_creation_sequence",
                schema: "inventory",
                table: "supply",
                column: "creation_sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_supply_name",
                schema: "inventory",
                table: "supply",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_movement",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "supply",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "supply_category",
                schema: "inventory");

            // No explicit DropSequence: supply_creation_sequence_seq is OWNED BY
            // inventory.supply.creation_sequence, so PostgreSQL already dropped it when the
            // table above was dropped.
        }
    }
}
