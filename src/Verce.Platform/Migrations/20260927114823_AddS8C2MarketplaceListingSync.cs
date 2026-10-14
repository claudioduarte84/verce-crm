using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddS8C2MarketplaceListingSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "provider_observed_at",
                schema: "commerce",
                table: "marketplace_listing_observation",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<int>(
                name: "variation_count",
                schema: "commerce",
                table: "marketplace_listing_observation",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "auto_link_suppressed",
                schema: "commerce",
                table: "marketplace_listing",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "latest_observation_fingerprint",
                schema: "commerce",
                table: "marketplace_listing",
                type: "char(64)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "linkage_source",
                schema: "commerce",
                table: "marketplace_listing",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sku_mapping_id",
                schema: "commerce",
                table: "marketplace_listing",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "variation_count",
                schema: "commerce",
                table: "marketplace_listing",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE commerce.marketplace_listing
                SET linkage_source = 'MANUAL'
                WHERE linkage_state = 'LINKED';

                -- Only a 64-character fingerprint (the V2 hash shape) can ever equal a future fingerprint and fits char(64). Legacy S8B observation
                -- fingerprints are client-supplied (varchar(128)); a longer one must stay NULL (= unknown) instead of aborting the migration with 22001.
                WITH observed AS (
                    SELECT marketplace_listing_id, fingerprint, ingested_at,
                           max(ingested_at) OVER (PARTITION BY marketplace_listing_id) AS latest_ingested_at
                    FROM commerce.marketplace_listing_observation
                ), latest AS (
                    SELECT marketplace_listing_id,
                           CASE WHEN count(DISTINCT fingerprint) FILTER (WHERE ingested_at = latest_ingested_at) = 1
                                THEN min(fingerprint) FILTER (WHERE ingested_at = latest_ingested_at)
                                ELSE NULL END AS fingerprint
                    FROM observed
                    GROUP BY marketplace_listing_id
                )
                UPDATE commerce.marketplace_listing AS listing
                SET latest_observation_fingerprint = latest.fingerprint
                FROM latest
                WHERE listing.id = latest.marketplace_listing_id
                  AND length(latest.fingerprint) = 64;
                """);

            migrationBuilder.CreateTable(
                name: "marketplace_account_sku_mapping",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    marketplace_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_sku = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    confirmed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    invalidated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invalidated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invalidated_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_account_sku_mapping", x => x.id);
                    table.CheckConstraint("ck_marketplace_account_sku_mapping_active", "active = (invalidated_at IS NULL AND invalidated_reason IS NULL)");
                    table.CheckConstraint("ck_marketplace_account_sku_mapping_reason", "invalidated_reason IS NULL OR invalidated_reason IN ('OPERATOR_DEACTIVATED','REPLACED_BY_NEW_MAPPING')");
                    table.ForeignKey(
                        name: "fk_marketplace_account_sku_mapping_marketplace_account_marketp",
                        column: x => x.marketplace_account_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_listing_sync_run",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    marketplace_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    phase = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    root_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    retry_of_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    enumeration_completed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    safe_result_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_failure_classification = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    provider_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    retry_after_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_operation_id_at_start = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_listing_sync_run", x => x.id);
                    table.CheckConstraint("ck_marketplace_listing_sync_run_ancestry", "(kind = 'FULL' AND retry_of_run_id IS NULL AND root_run_id = id) OR (kind = 'FAILED_ONLY' AND retry_of_run_id IS NOT NULL AND enumeration_completed = false)");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_attempts", "attempt_count >= 0 AND attempt_count <= max_attempts AND max_attempts BETWEEN 1 AND 10");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_full_terminal", "kind <> 'FULL' OR status NOT IN ('SUCCEEDED','PARTIAL') OR enumeration_completed = true");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_kind", "kind IN ('FULL','FAILED_ONLY')");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_lease", "(status = 'RUNNING') = (lease_token IS NOT NULL AND lease_until IS NOT NULL)");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_phase", "phase IS NULL OR phase IN ('ENUMERATING','RECONCILING_MISSING','DETAILING')");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_status", "status IN ('QUEUED','RUNNING','SUCCEEDED','PARTIAL','FAILED')");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_terminal", "(status IN ('SUCCEEDED','PARTIAL','FAILED')) = (finished_at IS NOT NULL AND safe_result_code IS NOT NULL AND lease_token IS NULL)");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_marketplace_listing_sync_run_marketplace_account_marketplac",
                        column: x => x.marketplace_account_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_listing_sync_run_marketplace_provider_provider_",
                        column: x => x.provider_code,
                        principalSchema: "commerce",
                        principalTable: "marketplace_provider",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_listing_sync_run_retry_of_run",
                        column: x => x.retry_of_run_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_listing_sync_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_listing_sync_run_root_run",
                        column: x => x.root_run_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_listing_sync_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_listing_sync_run_item",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sync_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_listing_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    origin = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    marketplace_listing_id = table.Column<Guid>(type: "uuid", nullable: true),
                    safe_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    listing_created = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    observation_appended = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_listing_sync_run_item", x => x.id);
                    table.CheckConstraint("ck_marketplace_listing_sync_run_item_attempts", "attempt_count >= 0");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_item_completion", "(outcome = 'PENDING') = (completed_at IS NULL)");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_item_origin", "origin IN ('ENUMERATED','KNOWN_NOT_ENUMERATED','RETRY_TARGET')");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_item_outcome", "outcome IN ('PENDING','FOUND','NOT_FOUND','ACCESS_DENIED','SELLER_MISMATCH','TRANSIENT_ERROR','PERMANENT_ERROR')");
                    table.CheckConstraint("ck_marketplace_listing_sync_run_item_result", "(outcome = 'FOUND' AND marketplace_listing_id IS NOT NULL AND safe_error_code IS NULL) OR (outcome = 'PENDING') OR (outcome <> 'FOUND' AND safe_error_code IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_marketplace_listing_sync_run_item_marketplace_listing_marke",
                        column: x => x.marketplace_listing_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_listing",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_listing_sync_run_item_marketplace_listing_sync_",
                        column: x => x.sync_run_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_listing_sync_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_listing_observation_variation_count",
                schema: "commerce",
                table: "marketplace_listing_observation",
                sql: "variation_count IS NULL OR variation_count >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_marketplace_account_id_sync_state",
                schema: "commerce",
                table: "marketplace_listing",
                columns: new[] { "marketplace_account_id", "sync_state" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sku_mapping_id",
                schema: "commerce",
                table: "marketplace_listing",
                column: "sku_mapping_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_listing_linkage_source",
                schema: "commerce",
                table: "marketplace_listing",
                sql: "linkage_source IS NULL OR linkage_source IN ('MANUAL','DETERMINISTIC_SKU')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_listing_linkage_source_required",
                schema: "commerce",
                table: "marketplace_listing",
                sql: "(linkage_state = 'LINKED') = (linkage_source IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_listing_sku_mapping",
                schema: "commerce",
                table: "marketplace_listing",
                sql: "(linkage_source = 'DETERMINISTIC_SKU') = (sku_mapping_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_listing_variation_count",
                schema: "commerce",
                table: "marketplace_listing",
                sql: "variation_count IS NULL OR variation_count >= 0");

            migrationBuilder.Sql("""
                INSERT INTO commerce.marketplace_provider (code, name)
                VALUES ('MERCADO_LIVRE', 'Mercado Livre')
                ON CONFLICT (code) DO NOTHING;

                INSERT INTO commerce.marketplace_provider_capability
                    (provider_code, capability_code, state, source, verified_at, updated_at)
                VALUES
                    ('MERCADO_LIVRE', 'LISTINGS_READ', 'SUPPORTED', 'DISCOVERY', TIMESTAMPTZ '2026-09-24T00:00:00Z', TIMESTAMPTZ '2026-09-24T00:00:00Z')
                ON CONFLICT (provider_code, capability_code) DO UPDATE
                SET state = EXCLUDED.state,
                    source = EXCLUDED.source,
                    verified_at = EXCLUDED.verified_at,
                    updated_at = EXCLUDED.updated_at;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_sku_mapping_marketplace_account_id_acti",
                schema: "commerce",
                table: "marketplace_account_sku_mapping",
                columns: new[] { "marketplace_account_id", "active" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_sku_mapping_marketplace_account_id_exte",
                schema: "commerce",
                table: "marketplace_account_sku_mapping",
                columns: new[] { "marketplace_account_id", "external_sku" },
                unique: true,
                filter: "active");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_sku_mapping_product_id",
                schema: "commerce",
                table: "marketplace_account_sku_mapping",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_run_marketplace_account_id",
                schema: "commerce",
                table: "marketplace_listing_sync_run",
                column: "marketplace_account_id",
                unique: true,
                filter: "status IN ('QUEUED','RUNNING')");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_run_marketplace_account_id_kind_re",
                schema: "commerce",
                table: "marketplace_listing_sync_run",
                columns: new[] { "marketplace_account_id", "kind", "requested_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_run_provider_code",
                schema: "commerce",
                table: "marketplace_listing_sync_run",
                column: "provider_code");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_run_retry_of_run_id",
                schema: "commerce",
                table: "marketplace_listing_sync_run",
                column: "retry_of_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_run_root_run_id_requested_at",
                schema: "commerce",
                table: "marketplace_listing_sync_run",
                columns: new[] { "root_run_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_run_status_lease_until",
                schema: "commerce",
                table: "marketplace_listing_sync_run",
                columns: new[] { "status", "lease_until" },
                filter: "status = 'RUNNING'");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_run_status_requested_at",
                schema: "commerce",
                table: "marketplace_listing_sync_run",
                columns: new[] { "status", "requested_at" },
                filter: "status IN ('QUEUED','RUNNING')");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_run_item_marketplace_listing_id",
                schema: "commerce",
                table: "marketplace_listing_sync_run_item",
                column: "marketplace_listing_id");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_run_item_sync_run_id_external_list",
                schema: "commerce",
                table: "marketplace_listing_sync_run_item",
                columns: new[] { "sync_run_id", "external_listing_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_listing_sync_run_item_sync_run_id_outcome_origi",
                schema: "commerce",
                table: "marketplace_listing_sync_run_item",
                columns: new[] { "sync_run_id", "outcome", "origin", "external_listing_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_marketplace_listing_marketplace_account_sku_mapping_sku_map",
                schema: "commerce",
                table: "marketplace_listing",
                column: "sku_mapping_id",
                principalSchema: "commerce",
                principalTable: "marketplace_account_sku_mapping",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE commerce.marketplace_provider_capability
                SET state = 'UNKNOWN',
                    source = 'MANUAL',
                    verified_at = NULL,
                    updated_at = TIMESTAMPTZ '2026-09-24T00:00:00Z'
                WHERE provider_code = 'MERCADO_LIVRE'
                  AND capability_code = 'LISTINGS_READ';
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_marketplace_listing_marketplace_account_sku_mapping_sku_map",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropTable(
                name: "marketplace_account_sku_mapping",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_listing_sync_run_item",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_listing_sync_run",
                schema: "commerce");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_listing_observation_variation_count",
                schema: "commerce",
                table: "marketplace_listing_observation");

            migrationBuilder.DropIndex(
                name: "ix_marketplace_listing_marketplace_account_id_sync_state",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropIndex(
                name: "ix_marketplace_listing_sku_mapping_id",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_listing_linkage_source",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_listing_linkage_source_required",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_listing_sku_mapping",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_listing_variation_count",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropColumn(
                name: "variation_count",
                schema: "commerce",
                table: "marketplace_listing_observation");

            migrationBuilder.DropColumn(
                name: "auto_link_suppressed",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropColumn(
                name: "latest_observation_fingerprint",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropColumn(
                name: "linkage_source",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropColumn(
                name: "sku_mapping_id",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.DropColumn(
                name: "variation_count",
                schema: "commerce",
                table: "marketplace_listing");

            migrationBuilder.Sql("""
                UPDATE commerce.marketplace_listing_observation
                SET provider_observed_at = ingested_at
                WHERE provider_observed_at IS NULL;
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "provider_observed_at",
                schema: "commerce",
                table: "marketplace_listing_observation",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
