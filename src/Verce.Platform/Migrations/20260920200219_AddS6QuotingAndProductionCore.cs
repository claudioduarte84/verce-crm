using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddS6QuotingAndProductionCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "production");

            migrationBuilder.EnsureSchema(
                name: "quoting");

            migrationBuilder.CreateTable(
                name: "production_order",
                schema: "production",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    number_date = table.Column<DateOnly>(type: "date", nullable: false),
                    number_sequence = table.Column<int>(type: "integer", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    has_pending_revision = table.Column<bool>(type: "boolean", nullable: false),
                    superseded_by_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_production_order", x => x.id);
                    table.CheckConstraint("ck_production_order_status", "status IN ('QUEUED','IN_PRODUCTION','READY','SHIPPED','DELIVERED','CANCELED')");
                    table.ForeignKey(
                        name: "fk_production_order_production_order_superseded_by_order_id",
                        column: x => x.superseded_by_order_id,
                        principalSchema: "production",
                        principalTable: "production_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quote",
                schema: "quoting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    number_date = table.Column<DateOnly>(type: "date", nullable: false),
                    number_sequence = table.Column<int>(type: "integer", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quote_revision",
                schema: "quoting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_index = table.Column<int>(type: "integer", nullable: false),
                    revision_suffix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    sales_channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    validity_days = table.Column<int>(type: "integer", nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    customer_document_snapshot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    customer_contacts_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    customer_addresses_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    superseded_by_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    subtotal_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    total_cost_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    expected_profit_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    effective_margin_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_revision", x => x.id);
                    table.CheckConstraint("ck_quote_revision_index_positive", "revision_index >= 1");
                    table.CheckConstraint("ck_quote_revision_status", "status IN ('GENERATED','SENT','NEGOTIATING','APPROVED','CANCELED','EXPIRED','SUPERSEDED')");
                    table.ForeignKey(
                        name: "fk_quote_revision_quote_quote_id",
                        column: x => x.quote_id,
                        principalSchema: "quoting",
                        principalTable: "quote",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_quote_revision_quote_revision_source_revision_id",
                        column: x => x.source_revision_id,
                        principalSchema: "quoting",
                        principalTable: "quote_revision",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quote_revision_quote_revision_superseded_by_revision_id",
                        column: x => x.superseded_by_revision_id,
                        principalSchema: "quoting",
                        principalTable: "quote_revision",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quote_item",
                schema: "quoting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    source_quote_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_recipe_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    unit_total_cost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    cost_engine_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    desired_margin_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    sales_channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fee_rule_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    commission_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    fixed_fee_application = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    raw_fixed_fee = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    allocated_order_fee = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    fixed_fee_per_unit = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    rounding_policy_applied = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    suggested_unit_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    commission_amount_per_unit = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    fee_clamp_applied = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    manual_price_override = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    price_overridden = table.Column<bool>(type: "boolean", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    discount_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    discount_value = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    net_unit_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    line_total_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    line_cost_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    line_fee_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    expected_profit_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    effective_margin_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_item", x => x.id);
                    table.CheckConstraint("ck_quote_item_discount_kind", "discount_kind IN ('None','Percent','Amount')");
                    table.CheckConstraint("ck_quote_item_fixed_fee_application", "fixed_fee_application IN ('PerUnit','PerOrder')");
                    table.CheckConstraint("ck_quote_item_manual_price_override_non_negative", "manual_price_override IS NULL OR manual_price_override >= 0");
                    table.CheckConstraint("ck_quote_item_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_quote_item_quote_revision_quote_revision_id",
                        column: x => x.quote_revision_id,
                        principalSchema: "quoting",
                        principalTable: "quote_revision",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quote_status_history",
                schema: "quoting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    to_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_status_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_quote_status_history_quote_revision_quote_revision_id",
                        column: x => x.quote_revision_id,
                        principalSchema: "quoting",
                        principalTable: "quote_revision",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quote_item_additional_cost_snapshot",
                schema: "quoting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_item_additional_cost_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "fk_quote_item_additional_cost_snapshot_quote_item_quote_item_id",
                        column: x => x.quote_item_id,
                        principalSchema: "quoting",
                        principalTable: "quote_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quote_item_cost_snapshot",
                schema: "quoting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engine_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    material_cost_before_wastage = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    material_wastage_cost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    materials_total_cost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    labor_minutes = table.Column<decimal>(type: "numeric(14,4)", nullable: true),
                    labor_hourly_rate = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    labor_rate_source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    labor_cost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    machine_minutes = table.Column<decimal>(type: "numeric(14,4)", nullable: true),
                    machine_hourly_rate = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    machine_cost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    additional_direct_costs_total = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    total_estimated_cost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    output_quantity = table.Column<int>(type: "integer", nullable: false),
                    estimated_unit_cost = table.Column<decimal>(type: "numeric(18,6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_item_cost_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "fk_quote_item_cost_snapshot_quote_item_quote_item_id",
                        column: x => x.quote_item_id,
                        principalSchema: "quoting",
                        principalTable: "quote_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quote_item_material_snapshot",
                schema: "quoting",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    supply_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supply_code_snapshot = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    supply_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entered_quantity = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    entered_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    normalized_quantity_base_unit = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    base_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    wastage_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    effective_quantity_base_unit = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    cost_source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cost_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    unit_cost_base_unit = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    cost_before_wastage = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    wastage_cost = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    cost_after_wastage = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    current_stock_base_unit_at_issue = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    exceeded_current_stock_at_issue = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_item_material_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "fk_quote_item_material_snapshot_quote_item_quote_item_id",
                        column: x => x.quote_item_id,
                        principalSchema: "quoting",
                        principalTable: "quote_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_production_order_number_date_number_sequence",
                schema: "production",
                table: "production_order",
                columns: new[] { "number_date", "number_sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_production_order_order_number",
                schema: "production",
                table: "production_order",
                column: "order_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_production_order_quote_id",
                schema: "production",
                table: "production_order",
                column: "quote_id");

            migrationBuilder.CreateIndex(
                name: "ix_production_order_quote_revision_id",
                schema: "production",
                table: "production_order",
                column: "quote_revision_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_production_order_status_number_date",
                schema: "production",
                table: "production_order",
                columns: new[] { "status", "number_date" });

            migrationBuilder.CreateIndex(
                name: "ix_production_order_superseded_by_order_id",
                schema: "production",
                table: "production_order",
                column: "superseded_by_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_quote_number",
                schema: "quoting",
                table: "quote",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quote_number_date_number_sequence",
                schema: "quoting",
                table: "quote",
                columns: new[] { "number_date", "number_sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quote_item_quote_revision_id_line_number",
                schema: "quoting",
                table: "quote_item",
                columns: new[] { "quote_revision_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quote_item_additional_cost_snapshot_quote_item_id_line_numb",
                schema: "quoting",
                table: "quote_item_additional_cost_snapshot",
                columns: new[] { "quote_item_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quote_item_cost_snapshot_quote_item_id",
                schema: "quoting",
                table: "quote_item_cost_snapshot",
                column: "quote_item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quote_item_material_snapshot_quote_item_id_line_number",
                schema: "quoting",
                table: "quote_item_material_snapshot",
                columns: new[] { "quote_item_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quote_revision_expiration_eligible",
                schema: "quoting",
                table: "quote_revision",
                column: "valid_until",
                filter: "superseded_by_revision_id IS NULL AND status IN ('GENERATED','SENT','NEGOTIATING')");

            migrationBuilder.CreateIndex(
                name: "ix_quote_revision_quote_id_revision_index",
                schema: "quoting",
                table: "quote_revision",
                columns: new[] { "quote_id", "revision_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quote_revision_source_revision_id",
                schema: "quoting",
                table: "quote_revision",
                column: "source_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_quote_revision_superseded_by_revision_id",
                schema: "quoting",
                table: "quote_revision",
                column: "superseded_by_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_quote_status_history_changed_at",
                schema: "quoting",
                table: "quote_status_history",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "ix_quote_status_history_quote_revision_id_changed_at",
                schema: "quoting",
                table: "quote_status_history",
                columns: new[] { "quote_revision_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_quote_status_history_to_status",
                schema: "quoting",
                table: "quote_status_history",
                column: "to_status");

            // ADR-0004 §1: shared per-business-date sequence counter for both Quoting ("QUOTE")
            // and Production ("PRODUCTION_ORDER") series. Deliberately NOT modeled as an EF entity
            // — mirrors the fee_rule_version_no_overlap EXCLUDE constraint precedent below — so
            // `has-pending-model-changes` never flags it as drift. See SequentialNumberAllocator.
            migrationBuilder.Sql("""
                CREATE TABLE quoting.quote_number_counter (
                    series text NOT NULL,
                    counter_date date NOT NULL,
                    last_sequence integer NOT NULL,
                    CONSTRAINT pk_quote_number_counter PRIMARY KEY (series, counter_date)
                );
                """);

            // CLAUDE.md rule 21 / H-04: Quote and its first QuoteRevision are inserted together in
            // one transaction, with a genuine FK cycle (Quote -> Revision via current_revision_id,
            // Revision -> Quote via quote_id). DEFERRABLE INITIALLY DEFERRED defers the check to
            // COMMIT time, when both rows exist — left unmodeled in the EF fluent config because
            // EF cannot express DEFERRABLE constraints.
            migrationBuilder.Sql("""
                ALTER TABLE quoting.quote
                    ADD CONSTRAINT fk_quote_current_revision_id FOREIGN KEY (current_revision_id)
                    REFERENCES quoting.quote_revision (id)
                    DEFERRABLE INITIALLY DEFERRED;
                """);

            // B-03: a PER_ORDER fixed fee is a BRL amount and must be exactly whole-cent precision.
            // NOT VALID means the constraint is enforced for all NEW/UPDATED rows going forward
            // without scanning/rejecting any pre-existing legacy S5 fee_rule_version rows.
            migrationBuilder.Sql("""
                ALTER TABLE pricing.fee_rule_version
                    ADD CONSTRAINT ck_fee_rule_version_fixed_fee_precision
                    CHECK (fixed_fee = round(fixed_fee, 2)) NOT VALID;
                """);
            migrationBuilder.Sql("""
                ALTER TABLE pricing.fee_rule_version
                    ADD CONSTRAINT ck_fee_rule_version_minimum_fee_precision
                    CHECK (minimum_fee IS NULL OR minimum_fee = round(minimum_fee, 2)) NOT VALID;
                """);
            migrationBuilder.Sql("""
                ALTER TABLE pricing.fee_rule_version
                    ADD CONSTRAINT ck_fee_rule_version_maximum_fee_precision
                    CHECK (maximum_fee IS NULL OR maximum_fee = round(maximum_fee, 2)) NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE pricing.fee_rule_version DROP CONSTRAINT ck_fee_rule_version_maximum_fee_precision;");
            migrationBuilder.Sql("ALTER TABLE pricing.fee_rule_version DROP CONSTRAINT ck_fee_rule_version_minimum_fee_precision;");
            migrationBuilder.Sql("ALTER TABLE pricing.fee_rule_version DROP CONSTRAINT ck_fee_rule_version_fixed_fee_precision;");
            migrationBuilder.Sql("ALTER TABLE quoting.quote DROP CONSTRAINT fk_quote_current_revision_id;");
            migrationBuilder.Sql("DROP TABLE quoting.quote_number_counter;");

            migrationBuilder.DropTable(
                name: "production_order",
                schema: "production");

            migrationBuilder.DropTable(
                name: "quote_item_additional_cost_snapshot",
                schema: "quoting");

            migrationBuilder.DropTable(
                name: "quote_item_cost_snapshot",
                schema: "quoting");

            migrationBuilder.DropTable(
                name: "quote_item_material_snapshot",
                schema: "quoting");

            migrationBuilder.DropTable(
                name: "quote_status_history",
                schema: "quoting");

            migrationBuilder.DropTable(
                name: "quote_item",
                schema: "quoting");

            migrationBuilder.DropTable(
                name: "quote_revision",
                schema: "quoting");

            migrationBuilder.DropTable(
                name: "quote",
                schema: "quoting");
        }
    }
}
