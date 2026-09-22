using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddS7DocumentTemplateEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "documents");

            migrationBuilder.AddColumn<string>(
                name: "delivery_terms",
                schema: "quoting",
                table: "quote_revision",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "internal_notes",
                schema: "quoting",
                table: "quote_revision",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "notes",
                schema: "quoting",
                table: "quote_revision",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "out_of_scope",
                schema: "quoting",
                table: "quote_revision",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_terms",
                schema: "quoting",
                table: "quote_revision",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scope",
                schema: "quoting",
                table: "quote_revision",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "technical_highlights",
                schema: "quoting",
                table: "quote_revision",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "technical_notes",
                schema: "quoting",
                table: "quote_revision",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title",
                schema: "quoting",
                table: "quote_revision",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "warranty",
                schema: "quoting",
                table: "quote_revision",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "document_type",
                schema: "documents",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_type", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "document_template",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("pk_document_template", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_template_document_type_document_type_code",
                        column: x => x.document_type_code,
                        principalSchema: "documents",
                        principalTable: "document_type",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_template_version",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    page_setup = table.Column<string>(type: "jsonb", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_template_version", x => x.id);
                    table.CheckConstraint("ck_document_template_version_status", "status IN ('DRAFT','PUBLISHED','ARCHIVED')");
                    table.ForeignKey(
                        name: "fk_document_template_version_document_template_document_templa",
                        column: x => x.document_template_id,
                        principalSchema: "documents",
                        principalTable: "document_template",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generated_document",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    render_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_template_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    render_data_snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    html_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    html_storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pdf_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    pdf_storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    pdf_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    chromium_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    render_engine_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    brand_asset_version_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    generated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reissue_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generated_document", x => x.id);
                    table.CheckConstraint("ck_generated_document_pdf_size_positive", "pdf_size_bytes > 0");
                    table.CheckConstraint("ck_generated_document_purpose", "purpose IN ('PREVIEW', 'ISSUED')");
                    table.ForeignKey(
                        name: "fk_generated_document_document_template_version_document_templ",
                        column: x => x.document_template_version_id,
                        principalSchema: "documents",
                        principalTable: "document_template_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_generated_document_document_type_document_type_code",
                        column: x => x.document_type_code,
                        principalSchema: "documents",
                        principalTable: "document_type",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_template_default_per_type",
                schema: "documents",
                table: "document_template",
                column: "document_type_code",
                unique: true,
                filter: "is_default AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_document_template_version_document_template_id_version_numb",
                schema: "documents",
                table: "document_template_version",
                columns: new[] { "document_template_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_template_version_one_open_draft",
                schema: "documents",
                table: "document_template_version",
                column: "document_template_id",
                unique: true,
                filter: "status = 'DRAFT'");

            migrationBuilder.CreateIndex(
                name: "ix_generated_document_current_per_source",
                schema: "documents",
                table: "generated_document",
                columns: new[] { "source_type", "source_id", "document_type_code" },
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "ix_generated_document_document_template_version_id",
                schema: "documents",
                table: "generated_document",
                column: "document_template_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_generated_document_document_type_code",
                schema: "documents",
                table: "generated_document",
                column: "document_type_code");

            migrationBuilder.CreateIndex(
                name: "ix_generated_document_pdf_sha256",
                schema: "documents",
                table: "generated_document",
                column: "pdf_sha256");

            migrationBuilder.CreateIndex(
                name: "ix_generated_document_render_request_id",
                schema: "documents",
                table: "generated_document",
                column: "render_request_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "generated_document",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "document_template_version",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "document_template",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "document_type",
                schema: "documents");

            migrationBuilder.DropColumn(
                name: "delivery_terms",
                schema: "quoting",
                table: "quote_revision");

            migrationBuilder.DropColumn(
                name: "internal_notes",
                schema: "quoting",
                table: "quote_revision");

            migrationBuilder.DropColumn(
                name: "notes",
                schema: "quoting",
                table: "quote_revision");

            migrationBuilder.DropColumn(
                name: "out_of_scope",
                schema: "quoting",
                table: "quote_revision");

            migrationBuilder.DropColumn(
                name: "payment_terms",
                schema: "quoting",
                table: "quote_revision");

            migrationBuilder.DropColumn(
                name: "scope",
                schema: "quoting",
                table: "quote_revision");

            migrationBuilder.DropColumn(
                name: "technical_highlights",
                schema: "quoting",
                table: "quote_revision");

            migrationBuilder.DropColumn(
                name: "technical_notes",
                schema: "quoting",
                table: "quote_revision");

            migrationBuilder.DropColumn(
                name: "title",
                schema: "quoting",
                table: "quote_revision");

            migrationBuilder.DropColumn(
                name: "warranty",
                schema: "quoting",
                table: "quote_revision");
        }
    }
}
