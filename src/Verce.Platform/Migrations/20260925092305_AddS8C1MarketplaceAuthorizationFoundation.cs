using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddS8C1MarketplaceAuthorizationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_marketplace_account_connection_state",
                schema: "commerce",
                table: "marketplace_account");

            migrationBuilder.DropColumn(
                name: "connection_state",
                schema: "commerce",
                table: "marketplace_account");

            migrationBuilder.DropColumn(
                name: "last_error",
                schema: "commerce",
                table: "marketplace_account");

            migrationBuilder.DropColumn(
                name: "last_failure_at",
                schema: "commerce",
                table: "marketplace_account");

            migrationBuilder.AddColumn<string>(
                name: "provider_reason_code",
                schema: "commerce",
                table: "marketplace_account_capability",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "commerce",
                table: "marketplace_account_capability",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LEGACY_MANUAL");

            migrationBuilder.CreateTable(
                name: "marketplace_authorization_session",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sales_channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    initiated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reconnect_marketplace_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reconnect_account_version = table.Column<long>(type: "bigint", nullable: true),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    browser_binding_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    safe_outcome_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    protected_transient_reference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    next_cleanup_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cleanup_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_authorization_session", x => x.id);
                    table.CheckConstraint("ck_marketplace_authorization_session_hashes", "length(state_hash) = 64 AND length(browser_binding_hash) = 64");
                    table.CheckConstraint("ck_marketplace_authorization_session_status", "status IN ('PENDING','CLAIMED','COMPLETED','FAILED','EXPIRED','REVOKED')");
                    table.ForeignKey(
                        name: "fk_marketplace_authorization_session_marketplace_provider_prov",
                        column: x => x.provider_code,
                        principalSchema: "commerce",
                        principalTable: "marketplace_provider",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_account_operation",
                schema: "commerce",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    phase = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    cleanup_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    marketplace_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    authorization_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resolved_external_account_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    expected_account_version = table.Column<long>(type: "bigint", nullable: true),
                    previous_credential_reference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    previous_confirmed_credential_version = table.Column<long>(type: "bigint", nullable: true),
                    candidate_credential_reference = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    candidate_credential_version = table.Column<long>(type: "bigint", nullable: true),
                    safe_result_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_cleanup_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cleanup_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_account_operation", x => x.id);
                    table.CheckConstraint("ck_marketplace_account_operation_cleanup", "cleanup_state IN ('NOT_REQUIRED','PENDING','DONE')");
                    table.CheckConstraint("ck_marketplace_account_operation_decision", "decision IN ('PENDING','CONFIRMED','FAIL_CLOSED')");
                    table.CheckConstraint("ck_marketplace_account_operation_phase", "phase IN ('PREPARED','EXTERNAL_IN_FLIGHT','SECRET_PERSISTED')");
                    table.ForeignKey(
                        name: "fk_marketplace_account_operation_marketplace_account_marketpla",
                        column: x => x.marketplace_account_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_marketplace_account_operation_marketplace_authorization_ses",
                        column: x => x.authorization_session_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_authorization_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_marketplace_account_operation_marketplace_provider_provider",
                        column: x => x.provider_code,
                        principalSchema: "commerce",
                        principalTable: "marketplace_provider",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_account_connection",
                schema: "commerce",
                columns: table => new
                {
                    marketplace_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorization_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    runtime_availability = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    identity_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_credential_version = table.Column<long>(type: "bigint", nullable: true),
                    confirmed_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    access_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_refresh_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_failure_classification = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    safe_failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_request_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    runtime_retry_after_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketplace_account_connection", x => x.marketplace_account_id);
                    table.CheckConstraint("ck_marketplace_account_connection_authorization", "authorization_state IN ('NOT_CONNECTED','CONNECTED','REAUTHORIZATION_REQUIRED','REVOKED')");
                    table.CheckConstraint("ck_marketplace_account_connection_runtime", "runtime_availability IN ('UNKNOWN','AVAILABLE','UNAVAILABLE')");
                    table.ForeignKey(
                        name: "fk_marketplace_account_connection_marketplace_account_marketpl",
                        column: x => x.marketplace_account_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_marketplace_account_connection_marketplace_account_operatio",
                        column: x => x.confirmed_operation_id,
                        principalSchema: "commerce",
                        principalTable: "marketplace_account_operation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            // S8B did not establish a protected-store receipt or a confirmed credential operation.
            // Preserve identities and commercial history, but clear the untrusted old pointer and
            // make every existing connection explicitly non-executable after the upgrade.
            migrationBuilder.Sql("UPDATE commerce.marketplace_account SET credential_reference = NULL;");
            migrationBuilder.Sql(@"
                INSERT INTO commerce.marketplace_account_connection
                    (marketplace_account_id, authorization_state, runtime_availability, created_at, updated_at)
                SELECT id, 'NOT_CONNECTED', 'UNKNOWN', now(), now()
                FROM commerce.marketplace_account;");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_connection_confirmed_operation_id",
                schema: "commerce",
                table: "marketplace_account_connection",
                column: "confirmed_operation_id",
                unique: true,
                filter: "confirmed_operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_operation_authorization_session_id",
                schema: "commerce",
                table: "marketplace_account_operation",
                column: "authorization_session_id",
                unique: true,
                filter: "authorization_session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_operation_marketplace_account_id",
                schema: "commerce",
                table: "marketplace_account_operation",
                column: "marketplace_account_id",
                unique: true,
                filter: "decision = 'PENDING' AND marketplace_account_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_operation_next_cleanup_at_created_at_id",
                schema: "commerce",
                table: "marketplace_account_operation",
                columns: new[] { "next_cleanup_at", "created_at", "id" },
                filter: "cleanup_state = 'PENDING'");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_operation_provider_code",
                schema: "commerce",
                table: "marketplace_account_operation",
                column: "provider_code",
                unique: true,
                filter: "decision = 'PENDING' AND phase IN ('EXTERNAL_IN_FLIGHT','SECRET_PERSISTED') AND kind IN ('CONNECT_NEW','RECONNECT')");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_account_operation_provider_code_decision_phase",
                schema: "commerce",
                table: "marketplace_account_operation",
                columns: new[] { "provider_code", "decision", "phase" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_authorization_session_next_cleanup_at_created_a",
                schema: "commerce",
                table: "marketplace_authorization_session",
                columns: new[] { "next_cleanup_at", "created_at", "id" },
                filter: "status IN ('COMPLETED','FAILED','EXPIRED','REVOKED')");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_authorization_session_provider_code",
                schema: "commerce",
                table: "marketplace_authorization_session",
                column: "provider_code");

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_authorization_session_reconnect_marketplace_acc",
                schema: "commerce",
                table: "marketplace_authorization_session",
                columns: new[] { "reconnect_marketplace_account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_authorization_session_state_hash",
                schema: "commerce",
                table: "marketplace_authorization_session",
                column: "state_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_authorization_session_status_claimed_at",
                schema: "commerce",
                table: "marketplace_authorization_session",
                columns: new[] { "status", "claimed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_marketplace_authorization_session_status_expires_at_created",
                schema: "commerce",
                table: "marketplace_authorization_session",
                columns: new[] { "status", "expires_at", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketplace_account_connection",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_account_operation",
                schema: "commerce");

            migrationBuilder.DropTable(
                name: "marketplace_authorization_session",
                schema: "commerce");

            migrationBuilder.DropColumn(
                name: "provider_reason_code",
                schema: "commerce",
                table: "marketplace_account_capability");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "commerce",
                table: "marketplace_account_capability");

            // ADR-0024 §6: "Migration down is conservative and cannot restore erased pointers;
            // re-auth is required after downgrade." The true prior connection_state was
            // intentionally discarded (never copied forward) by Up(), so this restored column
            // cannot recover it either — but it must still satisfy its own restored CHECK
            // constraint below for every existing row. NOT_CONFIGURED is the original enum's own
            // "no known connection state" member, i.e. the same safe-unknown posture Up() gives
            // every account (NOT_CONNECTED/UNKNOWN) — an empty-string default would violate
            // ck_marketplace_account_connection_state on any already-populated table.
            migrationBuilder.AddColumn<string>(
                name: "connection_state",
                schema: "commerce",
                table: "marketplace_account",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "NOT_CONFIGURED");

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                schema: "commerce",
                table: "marketplace_account",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_failure_at",
                schema: "commerce",
                table: "marketplace_account",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_marketplace_account_connection_state",
                schema: "commerce",
                table: "marketplace_account",
                sql: "connection_state IN ('NOT_CONFIGURED','DISCONNECTED','CONNECTED','ERROR')");
        }
    }
}
