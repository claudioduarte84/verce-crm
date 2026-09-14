using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddS2CustomersAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "settings");

            migrationBuilder.EnsureSchema(
                name: "customers");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateSequence(
                name: "customer_creation_sequence_seq",
                schema: "customers");

            migrationBuilder.CreateTable(
                name: "app_setting",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    value_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_secret = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_setting", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "brand_asset_type",
                schema: "settings",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_asset_type", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "company_profile",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    trade_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    document = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    website = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    instagram = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    whatsapp = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    zip_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    complement = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    district = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    state = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_profile", x => x.id);
                    table.CheckConstraint("ck_company_profile_singleton", "id = '00000000-0000-0000-0000-000000000001'");
                });

            migrationBuilder.CreateTable(
                name: "customer",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    trade_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    document = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    creation_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('customers.customer_creation_sequence_seq')"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer", x => x.id);
                    table.CheckConstraint("ck_customer_person_type", "person_type IN ('INDIVIDUAL', 'COMPANY')");
                });

            // ADR-0011 §1.2.1: the sequence is OWNED BY its column so PostgreSQL drops it
            // automatically alongside the table (see the matching note in Down below) — no
            // separate DropSequence is issued for it.
            migrationBuilder.Sql(
                "ALTER SEQUENCE customers.customer_creation_sequence_seq OWNED BY customers.customer.creation_sequence;");

            migrationBuilder.CreateTable(
                name: "brand_asset",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_asset_type_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_asset", x => x.id);
                    table.ForeignKey(
                        name: "fk_brand_asset_brand_asset_type_brand_asset_type_code",
                        column: x => x.brand_asset_type_code,
                        principalSchema: "settings",
                        principalTable: "brand_asset_type",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_address",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    zip_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    street = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    complement = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    district = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    is_default_shipping = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_address", x => x.id);
                    table.ForeignKey(
                        name: "fk_customer_address_customer_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "customers",
                        principalTable: "customer",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "brand_asset_version",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    file_path = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    width_px = table.Column<int>(type: "integer", nullable: false),
                    height_px = table.Column<int>(type: "integer", nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: true),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_asset_version", x => x.id);
                    table.CheckConstraint("ck_brand_asset_version_content_type", "content_type IN ('image/png', 'image/jpeg', 'image/webp')");
                    table.CheckConstraint("ck_brand_asset_version_file_size", "file_size_bytes > 0");
                    table.ForeignKey(
                        name: "fk_brand_asset_version_brand_asset_brand_asset_id",
                        column: x => x.brand_asset_id,
                        principalSchema: "settings",
                        principalTable: "brand_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "branding_assignment",
                schema: "settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    brand_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_branding_assignment", x => x.id);
                    table.CheckConstraint("ck_branding_assignment_role", "role IN ('SYSTEM_LOGO', 'SYSTEM_LOGO_COMPACT', 'FAVICON', 'DOCUMENT_DEFAULT_LOGO')");
                    table.ForeignKey(
                        name: "fk_branding_assignment_brand_asset_brand_asset_id",
                        column: x => x.brand_asset_id,
                        principalSchema: "settings",
                        principalTable: "brand_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_setting_key",
                schema: "settings",
                table: "app_setting",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_brand_asset_brand_asset_type_code",
                schema: "settings",
                table: "brand_asset",
                column: "brand_asset_type_code",
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_brand_asset_version_brand_asset_id",
                schema: "settings",
                table: "brand_asset_version",
                column: "brand_asset_id",
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "ix_brand_asset_version_brand_asset_id_version_number",
                schema: "settings",
                table: "brand_asset_version",
                columns: new[] { "brand_asset_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_brand_asset_version_sha256",
                schema: "settings",
                table: "brand_asset_version",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "ix_branding_assignment_brand_asset_id",
                schema: "settings",
                table: "branding_assignment",
                column: "brand_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_branding_assignment_role",
                schema: "settings",
                table: "branding_assignment",
                column: "role",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_creation_sequence",
                schema: "customers",
                table: "customer",
                column: "creation_sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_customer_document",
                schema: "customers",
                table: "customer",
                column: "document",
                unique: true,
                filter: "document IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_customer_is_active",
                schema: "customers",
                table: "customer",
                column: "is_active",
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_customer_name",
                schema: "customers",
                table: "customer",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_address_customer_id",
                schema: "customers",
                table: "customer_address",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_address_customer_id1",
                schema: "customers",
                table: "customer_address",
                column: "customer_id",
                unique: true,
                filter: "is_default_shipping");

            migrationBuilder.CreateIndex(
                name: "ix_customer_address_customer_id2",
                schema: "customers",
                table: "customer_address",
                column: "customer_id",
                unique: true,
                filter: "is_primary");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_setting",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "brand_asset_version",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "branding_assignment",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "company_profile",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "customer_address",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "brand_asset",
                schema: "settings");

            migrationBuilder.DropTable(
                name: "customer",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "brand_asset_type",
                schema: "settings");

            // No explicit DropSequence: customer_creation_sequence_seq is OWNED BY
            // customer.creation_sequence (set in Up via raw SQL), so PostgreSQL already dropped
            // it automatically as part of DropTable("customer") above. An explicit drop here
            // would fail with "sequence does not exist".

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
