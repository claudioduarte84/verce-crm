using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class AddDataProtectionKeyArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_protection_key_archive",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_key_id = table.Column<int>(type: "integer", nullable: false),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_by = table.Column<Guid>(type: "uuid", nullable: true),
                    archive_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    recovery_operation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_key_archive", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_protection_key_archive_recovery_operation_id",
                schema: "platform",
                table: "data_protection_key_archive",
                column: "recovery_operation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_protection_key_archive",
                schema: "platform");
        }
    }
}
