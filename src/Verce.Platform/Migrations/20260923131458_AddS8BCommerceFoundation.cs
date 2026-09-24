using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddS8BCommerceFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "commerce");

            migrationBuilder.CreateTable(
                name: "channel_offer",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    intended_unit_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    price_source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    seller_paid_shipping_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_offer", x => x.id);
                    table.CheckConstraint("ck_channel_offer_price", "intended_unit_price > 0");
                    table.CheckConstraint("ck_channel_offer_price_source", "price_source IN ('PRICING_ENGINE','MANUAL','IMPORTED_OBSERVED')");
                    table.CheckConstraint("ck_channel_offer_shipping", "seller_paid_shipping_amount IS NULL OR seller_paid_shipping_amount >= 0");
                    table.CheckConstraint("ck_channel_offer_status", "status IN ('ACTIVE','INACTIVE')");
                });

            migrationBuilder.CreateTable(
                name: "commercial_tag",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commercial_tag", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_account",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_account_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sales_channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    credential_reference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    connection_state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    sync_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_sync_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_successful_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_account", x => x.id);
                    table.CheckConstraint("ck_marketplace_account_connection_state", "connection_state IN ('NOT_CONFIGURED','DISCONNECTED','CONNECTED','ERROR')");
                    table.CheckConstraint("ck_marketplace_account_sync_state", "sync_state IN ('NEVER_SYNCED','SYNCED','ERROR')");
                });

            migrationBuilder.CreateTable(
                name: "marketplace_listing",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    marketplace_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_listing_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    external_sku = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel_offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title_snapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    observed_price = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    listing_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    observed_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_native_status = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    linkage_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    sync_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_successful_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sync_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_listing", x => x.id);
                    table.CheckConstraint("ck_marketplace_listing_linkage", "(linkage_state = 'LINKED' AND product_id IS NOT NULL) OR (linkage_state IN ('UNLINKED','NEEDS_REVIEW') AND product_id IS NULL AND channel_offer_id IS NULL)");
                    table.CheckConstraint("ck_marketplace_listing_observed_price", "observed_price IS NULL OR observed_price > 0");
                    table.CheckConstraint("ck_marketplace_listing_offer_linkage", "channel_offer_id IS NULL OR linkage_state = 'LINKED'");
                });

            migrationBuilder.CreateTable(
                name: "marketplace_provider",
                schema: "commerce",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_provider", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_provider_capability",
                schema: "commerce",
                columns: table => new
                {
                    provider_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    capability_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_provider_capability", x => new { x.provider_code, x.capability_code });
                    table.CheckConstraint("ck_marketplace_provider_capability_state", "state IN ('UNKNOWN','SUPPORTED','UNSUPPORTED')");
                });

            migrationBuilder.CreateTable(
                name: "product_commercial_profile",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_commercial_profile", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_account_capability",
                schema: "commerce",
                columns: table => new
                {
                    marketplace_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_account_capability", x => new { x.marketplace_account_id, x.capability_code });
                    table.CheckConstraint("ck_marketplace_account_capability_state", "state IN ('UNKNOWN','GRANTED','DENIED')");
                    table.ForeignKey(
                        name: "fk_marketplace_account_capability_marketplace_account_marketpl",
                        column: x => x.marketplace_account_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_listing_observation",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    marketplace_listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_sku = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    title_snapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    observed_price = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    observed_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_native_status = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    observation_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provenance = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_listing_observation", x => x.id);
                    table.ForeignKey(
                        name: "fk_marketplace_listing_observation_marketplace_listing_marketp",
                        column: x => x.marketplace_listing_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_listing",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_listing_tag",
                schema: "commerce",
                columns: table => new
                {
                    marketplace_listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commercial_tag_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_listing_tag", x => new { x.marketplace_listing_id, x.commercial_tag_id });
                    table.ForeignKey(
                        name: "fk_marketplace_listing_tag_marketplace_listing_marketplace_lis",
                        column: x => x.marketplace_listing_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_listing",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_commercial_image",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_commercial_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    alt_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_commercial_image", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_commercial_image_product_commercial_profile_product",
                        column: x => x.product_commercial_profile_id,
                        principalSchema: "commerce",
                        principalTable: "product_commercial_profile",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_commercial_tag",
                schema: "commerce",
                columns: table => new
                {
                    product_commercial_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    commercial_tag_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_commercial_tag", x => new { x.product_commercial_profile_id, x.commercial_tag_id });
                    table.ForeignKey(
                        name: "fk_product_commercial_tag_product_commercial_profile_product_c",
                        column: x => x.product_commercial_profile_id,
                        principalSchema: "commerce",
                        principalTable: "product_commercial_profile",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_channel_offer_product_id",
                schema: "commerce",
                table: "channel_offer",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_offer_product_id_sales_channel_id",
                schema: "commerce",
                table: "channel_offer",
                columns: new[] { "product_id", "sales_channel_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_channel_offer_sales_channel_id_status",
                schema: "commerce",
                table: "channel_offer",
                columns: new[] { "sales_channel_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_commercial_tag_active",
                schema: "commerce",
                table: "commercial_tag",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_commercial_tag_code",
                schema: "commerce",
                table: "commercial_tag",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_provider_code_active",
                schema: "commerce",
                table: "marketplace_account",
                columns: new[] { "provider_code", "active" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_provider_code_external_account_id",
                schema: "commerce",
                table: "marketplace_account",
                columns: new[] { "provider_code", "external_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_sales_channel_id_active",
                schema: "commerce",
                table: "marketplace_account",
                columns: new[] { "sales_channel_id", "active" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_sync_state",
                schema: "commerce",
                table: "marketplace_account",
                column: "sync_state");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_channel_offer_id",
                schema: "commerce",
                table: "marketplace_listing",
                column: "channel_offer_id");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_linkage_state",
                schema: "commerce",
                table: "marketplace_listing",
                column: "linkage_state");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_marketplace_account_id_external_listing",
                schema: "commerce",
                table: "marketplace_listing",
                columns: new[] { "marketplace_account_id", "external_listing_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_marketplace_account_id_external_sku",
                schema: "commerce",
                table: "marketplace_listing",
                columns: new[] { "marketplace_account_id", "external_sku" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_observed_status",
                schema: "commerce",
                table: "marketplace_listing",
                column: "observed_status");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_product_id",
                schema: "commerce",
                table: "marketplace_listing",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_state",
                schema: "commerce",
                table: "marketplace_listing",
                column: "sync_state");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_observation_marketplace_listing_id_obse",
                schema: "commerce",
                table: "marketplace_listing_observation",
                columns: new[] { "marketplace_listing_id", "observation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_observation_marketplace_listing_id_prov",
                schema: "commerce",
                table: "marketplace_listing_observation",
                columns: new[] { "marketplace_listing_id", "provider_observed_at", "ingested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_product_commercial_image_product_commercial_profile_id",
                schema: "commerce",
                table: "product_commercial_image",
                column: "product_commercial_profile_id",
                unique: true,
                filter: "role = 'PRIMARY'");

            migrationBuilder.CreateIndex(
                name: "ix_product_commercial_image_profile_asset",
                schema: "commerce",
                table: "product_commercial_image",
                columns: new[] { "product_commercial_profile_id", "brand_asset_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_commercial_image_product_commercial_profile_id_sort",
                schema: "commerce",
                table: "product_commercial_image",
                columns: new[] { "product_commercial_profile_id", "sort_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_commercial_profile_product_id",
                schema: "commerce",
                table: "product_commercial_profile",
                column: "product_id",
                unique: true);

            migrationBuilder.CreateIndex(name: "ix_product_commercial_tag_commercial_tag_id", schema: "commerce", table: "product_commercial_tag", column: "commercial_tag_id");
            migrationBuilder.CreateIndex(name: "ix_marketplace_listing_tag_commercial_tag_id", schema: "commerce", table: "marketplace_listing_tag", column: "commercial_tag_id");
            migrationBuilder.AddForeignKey(name: "fk_marketplace_account_marketplace_provider_provider_code", schema: "commerce", table: "marketplace_account", column: "provider_code", principalSchema: "commerce", principalTable: "marketplace_provider", principalColumn: "code", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "fk_marketplace_provider_capability_marketplace_provider_provid", schema: "commerce", table: "marketplace_provider_capability", column: "provider_code", principalSchema: "commerce", principalTable: "marketplace_provider", principalColumn: "code", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "fk_marketplace_listing_marketplace_account_marketplace_account", schema: "commerce", table: "marketplace_listing", column: "marketplace_account_id", principalSchema: "commerce", principalTable: "marketplace_account", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "fk_marketplace_listing_channel_offer_channel_offer_id", schema: "commerce", table: "marketplace_listing", column: "channel_offer_id", principalSchema: "commerce", principalTable: "channel_offer", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "fk_marketplace_listing_tag_commercial_tag_commercial_tag_id", schema: "commerce", table: "marketplace_listing_tag", column: "commercial_tag_id", principalSchema: "commerce", principalTable: "commercial_tag", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "fk_product_commercial_tag_commercial_tag_commercial_tag_id", schema: "commerce", table: "product_commercial_tag", column: "commercial_tag_id", principalSchema: "commerce", principalTable: "commercial_tag", principalColumn: "id", onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "fk_product_commercial_tag_commercial_tag_commercial_tag_id", table: "product_commercial_tag", schema: "commerce");
            migrationBuilder.DropForeignKey(name: "fk_marketplace_listing_tag_commercial_tag_commercial_tag_id", table: "marketplace_listing_tag", schema: "commerce");
            migrationBuilder.DropForeignKey(name: "fk_marketplace_listing_channel_offer_channel_offer_id", table: "marketplace_listing", schema: "commerce");
            migrationBuilder.DropForeignKey(name: "fk_marketplace_listing_marketplace_account_marketplace_account", table: "marketplace_listing", schema: "commerce");
            migrationBuilder.DropForeignKey(name: "fk_marketplace_provider_capability_marketplace_provider_provid", table: "marketplace_provider_capability", schema: "commerce");
            migrationBuilder.DropForeignKey(name: "fk_marketplace_account_marketplace_provider_provider_code", table: "marketplace_account", schema: "commerce");
            migrationBuilder.DropIndex(name: "ix_product_commercial_tag_commercial_tag_id", schema: "commerce", table: "product_commercial_tag");
            migrationBuilder.DropIndex(name: "ix_marketplace_listing_tag_commercial_tag_id", schema: "commerce", table: "marketplace_listing_tag");
            migrationBuilder.DropTable(
                name: "channel_offer",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "commercial_tag",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_account_capability",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_listing_observation",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_listing_tag",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_provider",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_provider_capability",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "product_commercial_image",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "product_commercial_tag",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_account",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_listing",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "product_commercial_profile",
                schema: "commerce");
        }
    }
}
