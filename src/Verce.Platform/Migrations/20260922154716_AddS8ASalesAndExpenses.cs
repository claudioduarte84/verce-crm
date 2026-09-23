using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddS8ASalesAndExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "finance");

            migrationBuilder.EnsureSchema(
                name: "sales");

            migrationBuilder.AddColumn<Guid>(
                name: "bracket_id",
                schema: "quoting",
                table: "quote_item",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bracket_resolution",
                schema: "quoting",
                table: "quote_item",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "fee_basis_amount",
                schema: "quoting",
                table: "quote_item",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "expense_category",
                schema: "finance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    default_treatment = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expense_category", x => x.id);
                    table.CheckConstraint("ck_expense_category_default_treatment", "default_treatment IN ('OPERATING_EXPENSE','INVENTORY_PURCHASE','ASSET_ACQUISITION')");
                });

            migrationBuilder.CreateTable(
                name: "price_bracket",
                schema: "pricing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fee_rule_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    min_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    max_price = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    commission_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    fixed_fee = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    minimum_fee = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    maximum_fee = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_bracket", x => x.id);
                    table.CheckConstraint("ck_price_bracket_commission", "commission_percent >= 0 AND commission_percent < 1");
                    table.CheckConstraint("ck_price_bracket_fee_range", "minimum_fee IS NULL OR maximum_fee IS NULL OR minimum_fee <= maximum_fee");
                    table.CheckConstraint("ck_price_bracket_fixed_fee", "fixed_fee >= 0");
                    table.CheckConstraint("ck_price_bracket_range", "min_price >= 0 AND (max_price IS NULL OR max_price > min_price)");
                    table.ForeignKey(
                        name: "fk_price_bracket_fee_rule_version_fee_rule_version_id",
                        column: x => x.fee_rule_version_id,
                        principalSchema: "pricing",
                        principalTable: "fee_rule_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sale",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    number_date = table.Column<DateOnly>(type: "date", nullable: false),
                    number_sequence = table.Column<int>(type: "integer", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_name_snapshot = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    sales_channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    conversion_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    marketplace_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_order_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    fee_source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    sold_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sold_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    gross_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    net_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    channel_fee_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    shipping_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    total_cost_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    gross_profit_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    effective_margin_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    cost_basis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    external_order_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale", x => x.id);
                    table.CheckConstraint("ck_sale_cost_basis", "cost_basis IN ('ESTIMATED','MIXED','ACTUAL')");
                    table.CheckConstraint("ck_sale_fee_source", "fee_source IN ('LOCAL_RULE','PROVIDER_REPORTED')");
                    table.CheckConstraint("ck_sale_source", "(source = 'QUOTE_CONVERSION' AND quote_revision_id IS NOT NULL AND marketplace_account_id IS NULL AND external_order_id IS NULL) OR (source = 'MANUAL_ENTRY' AND quote_revision_id IS NULL AND marketplace_account_id IS NULL AND external_order_id IS NULL) OR (source = 'MARKETPLACE_ORDER' AND quote_revision_id IS NULL AND marketplace_account_id IS NOT NULL AND external_order_id IS NOT NULL)");
                    table.CheckConstraint("ck_sale_status", "status IN ('CONFIRMED','CANCELED')");
                });

            migrationBuilder.CreateTable(
                name: "expense",
                schema: "finance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    expense_category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    incurred_on = table.Column<DateOnly>(type: "date", nullable: false),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: true),
                    payment_method = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    supplier_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    document_number = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    accounting_treatment = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    inventory_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sales_channel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    machine_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attachment_path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expense", x => x.id);
                    table.CheckConstraint("ck_expense_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_expense_inventory_movement_treatment", "inventory_movement_id IS NULL OR accounting_treatment = 'INVENTORY_PURCHASE'");
                    table.CheckConstraint("ck_expense_treatment", "accounting_treatment IN ('OPERATING_EXPENSE','INVENTORY_PURCHASE','ASSET_ACQUISITION')");
                    table.ForeignKey(
                        name: "fk_expense_expense_category_expense_category_id",
                        column: x => x.expense_category_id,
                        principalSchema: "finance",
                        principalTable: "expense_category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_item",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_name_snapshot = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    line_total_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    unit_cost_amount = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    line_cost_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    channel_fee_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    gross_profit_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    effective_margin_percent = table.Column<decimal>(type: "numeric(9,6)", nullable: false),
                    quote_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_sale_item_sale_sale_id",
                        column: x => x.sale_id,
                        principalSchema: "sales",
                        principalTable: "sale",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sale_status_history",
                schema: "sales",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    to_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale_status_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_sale_status_history_sale_sale_id",
                        column: x => x.sale_id,
                        principalSchema: "sales",
                        principalTable: "sale",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_expense_expense_category_id",
                schema: "finance",
                table: "expense",
                column: "expense_category_id");

            migrationBuilder.CreateIndex(
                name: "ix_expense_inventory_movement_id",
                schema: "finance",
                table: "expense",
                column: "inventory_movement_id",
                unique: true,
                filter: "inventory_movement_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_expense_operating_incurred_on",
                schema: "finance",
                table: "expense",
                column: "incurred_on",
                filter: "accounting_treatment = 'OPERATING_EXPENSE'");

            migrationBuilder.CreateIndex(
                name: "ix_expense_sales_channel_id_incurred_on",
                schema: "finance",
                table: "expense",
                columns: new[] { "sales_channel_id", "incurred_on" });

            migrationBuilder.CreateIndex(
                name: "ix_expense_category_name",
                schema: "finance",
                table: "expense_category",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_bracket_fee_rule_version_id_sort_order",
                schema: "pricing",
                table: "price_bracket",
                columns: new[] { "fee_rule_version_id", "sort_order" },
                unique: true);

            // ADR-0022 / CR-07.5: an interval may never overlap another interval of the same
            // immutable fee version. EF Core has no fluent representation for PostgreSQL
            // EXCLUDE constraints; btree_gist was enabled by the existing S5 migration.
            migrationBuilder.Sql("""
                ALTER TABLE pricing.price_bracket
                ADD CONSTRAINT price_bracket_no_overlap
                EXCLUDE USING gist (
                    fee_rule_version_id WITH =,
                    numrange(min_price, max_price, '[)') WITH &&
                )
                """);

            migrationBuilder.CreateIndex(
                name: "ix_sale_confirmed_sold_date",
                schema: "sales",
                table: "sale",
                column: "sold_date",
                filter: "status = 'CONFIRMED'");

            migrationBuilder.CreateIndex(
                name: "ix_sale_conversion_request_id",
                schema: "sales",
                table: "sale",
                column: "conversion_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sale_customer_id",
                schema: "sales",
                table: "sale",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_sale_marketplace_account_id_external_order_id",
                schema: "sales",
                table: "sale",
                columns: new[] { "marketplace_account_id", "external_order_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sale_quote_revision_id",
                schema: "sales",
                table: "sale",
                column: "quote_revision_id",
                unique: true,
                filter: "quote_revision_id IS NOT NULL AND status <> 'CANCELED'");

            migrationBuilder.CreateIndex(
                name: "ix_sale_sale_number",
                schema: "sales",
                table: "sale",
                column: "sale_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sale_sales_channel_id_sold_date",
                schema: "sales",
                table: "sale",
                columns: new[] { "sales_channel_id", "sold_date" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_item_sale_id_line_number",
                schema: "sales",
                table: "sale_item",
                columns: new[] { "sale_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sale_status_history_sale_id_changed_at",
                schema: "sales",
                table: "sale_status_history",
                columns: new[] { "sale_id", "changed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "expense",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "price_bracket",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "sale_item",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "sale_status_history",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "expense_category",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "sale",
                schema: "sales");

            migrationBuilder.DropColumn(
                name: "bracket_id",
                schema: "quoting",
                table: "quote_item");

            migrationBuilder.DropColumn(
                name: "bracket_resolution",
                schema: "quoting",
                table: "quote_item");

            migrationBuilder.DropColumn(
                name: "fee_basis_amount",
                schema: "quoting",
                table: "quote_item");
        }
    }
}
