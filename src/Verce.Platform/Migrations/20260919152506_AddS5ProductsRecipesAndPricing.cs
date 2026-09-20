using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddS5ProductsRecipesAndPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pricing");

            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateSequence(
                name: "product_creation_sequence_seq",
                schema: "catalog");

            migrationBuilder.CreateTable(
                name: "fee_rule",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fee_rule", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "product",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    creation_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('catalog.product_creation_sequence_seq')"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sales_channel",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    default_margin_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_channel", x => x.id);
                    table.CheckConstraint("ck_sales_channel_kind", "kind IN ('Direct', 'Marketplace', 'Other')");
                });

            migrationBuilder.CreateTable(
                name: "fee_rule_version",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fee_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    commission_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    fixed_fee = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    fixed_fee_application = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    minimum_fee = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    maximum_fee = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fee_rule_version", x => x.id);
                    table.CheckConstraint("ck_fee_rule_version_fixed_fee_application", "fixed_fee_application IN ('PerUnit', 'PerOrder')");
                    table.ForeignKey(
                        name: "fk_fee_rule_version_fee_rule_fee_rule_id",
                        column: x => x.fee_rule_id,
                        principalSchema: "pricing",
                        principalTable: "fee_rule",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_recipe",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    wastage_percent_override = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    labor_minutes = table.Column<decimal>(type: "numeric(14,4)", nullable: true),
                    labor_hourly_rate_override = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    machine_minutes = table.Column<decimal>(type: "numeric(14,4)", nullable: true),
                    machine_hourly_rate = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    output_quantity = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_recipe", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_recipe_product_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_recipe_additional_cost_line",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_recipe_additional_cost_line", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_recipe_additional_cost_line_product_recipe_product_",
                        column: x => x.product_recipe_id,
                        principalSchema: "catalog",
                        principalTable: "product_recipe",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_recipe_material_line",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_recipe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supply_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entered_quantity = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    entered_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    normalized_quantity_base_unit = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    wastage_percent_override = table.Column<decimal>(type: "numeric(9,6)", nullable: true),
                    manual_unit_cost_override = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_recipe_material_line", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_recipe_material_line_product_recipe_product_recipe_",
                        column: x => x.product_recipe_id,
                        principalSchema: "catalog",
                        principalTable: "product_recipe",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fee_rule_sales_channel_id",
                schema: "pricing",
                table: "fee_rule",
                column: "sales_channel_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fee_rule_version_fee_rule_id_valid_from",
                schema: "pricing",
                table: "fee_rule_version",
                columns: new[] { "fee_rule_id", "valid_from" });

            migrationBuilder.CreateIndex(
                name: "ix_product_active",
                schema: "catalog",
                table: "product",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_product_code",
                schema: "catalog",
                table: "product",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_creation_sequence",
                schema: "catalog",
                table: "product",
                column: "creation_sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_name",
                schema: "catalog",
                table: "product",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_product_recipe_product_id",
                schema: "catalog",
                table: "product_recipe",
                column: "product_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_recipe_additional_cost_line_product_recipe_id_sort_",
                schema: "catalog",
                table: "product_recipe_additional_cost_line",
                columns: new[] { "product_recipe_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_product_recipe_material_line_product_recipe_id_sort_order",
                schema: "catalog",
                table: "product_recipe_material_line",
                columns: new[] { "product_recipe_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_product_recipe_material_line_supply_id",
                schema: "catalog",
                table: "product_recipe_material_line",
                column: "supply_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_channel_active",
                schema: "pricing",
                table: "sales_channel",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_sales_channel_code",
                schema: "pricing",
                table: "sales_channel",
                column: "code",
                unique: true);

            // ADR-0005 §1: versions of the same FeeRule must never overlap in time — enforced by
            // the database, not application sequencing (mission §86). Npgsql/EF Core has no
            // fluent builder for EXCLUDE constraints, so this is raw SQL, deliberately left
            // unmodeled in PricingConfiguration.cs (see its remarks) so it never appears as
            // "pending model changes". `valid_until IS NULL` reads as `daterange(valid_from,
            // NULL, '[)')`, i.e. `[valid_from, infinity)` — an open-ended version correctly
            // conflicts with any later version that would otherwise leave the timeline ambiguous.
            migrationBuilder.Sql("""
                ALTER TABLE pricing.fee_rule_version
                  ADD CONSTRAINT fee_rule_version_no_overlap
                  EXCLUDE USING gist (
                      fee_rule_id WITH =,
                      daterange(valid_from, valid_until, '[)') WITH &&
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE pricing.fee_rule_version DROP CONSTRAINT fee_rule_version_no_overlap;");

            migrationBuilder.DropTable(
                name: "fee_rule_version",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "product_recipe_additional_cost_line",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_recipe_material_line",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "sales_channel",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "fee_rule",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "product_recipe",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product",
                schema: "catalog");

            migrationBuilder.DropSequence(
                name: "product_creation_sequence_seq",
                schema: "catalog");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:btree_gist", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
