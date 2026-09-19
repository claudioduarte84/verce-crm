using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Verce.Platform.Migrations
{
    /// <inheritdoc />
    public partial class ConvertCostingWastageRateToPercentagePoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // S3 stored this one setting as a fraction (0.05 = 5%).  S4 deliberately
            // uses percentage points (5 = 5%).  PostgreSQL's numeric cast is intentional:
            // malformed legacy values fail the migration rather than being silently changed
            // (the row is simply skipped by WHERE on a fresh database with no seeded row yet).
            // `trim_scale` drops mathematically unnecessary trailing zeros (0.05 -> "5", not
            // "5.00") without any floating-point formatting. `version = version + 1` is
            // deliberate (H-08): this migration changes the SEMANTIC MEANING of Value, not just
            // its text, so a stale editor/client that read Version N before deployment must have
            // its write rejected by ordinary optimistic concurrency after deployment — it must
            // never be able to silently resurrect the old fractional interpretation.
            migrationBuilder.Sql("""
                UPDATE settings.app_setting
                SET value = trim_scale(value::numeric * 100)::text,
                    version = version + 1
                WHERE key = 'costing.default_wastage_rate';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback still moves the concurrency token forward, never backward (ADR-0011 §2):
            // a version number must stay monotonic through a downgrade or a stale post-downgrade
            // client could collide with a pre-downgrade one that happened to read the same value.
            migrationBuilder.Sql("""
                UPDATE settings.app_setting
                SET value = trim_scale(value::numeric / 100)::text,
                    version = version + 1
                WHERE key = 'costing.default_wastage_rate';
                """);
        }
    }
}
