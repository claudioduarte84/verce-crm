using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Verce.Modules.Catalog;
using Verce.Modules.Pricing;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S8C1;

/// <summary>
/// M-S8C1-001 (Codex Sol regate finding): real, hermetic migration certification for
/// <c>20260925092305_AddS8C1MarketplaceAuthorizationFoundation</c>, its own disposable
/// PostgreSQL container(s), own data, disposing itself, no dependency on any other test or seed
/// order. Two dedicated containers: one purely for the "fresh zero-to-head" phase, one for the
/// "real S8B predecessor state -> upgrade -> downgrade -> reapply" story, so neither phase can
/// observe state the other left behind.
/// </summary>
public sealed class S8BToS8C1UpgradeTests : IAsyncLifetime
{
    private const string S8BFinalMigration = "20260923131458_AddS8BCommerceFoundation";
    private const string S8C1Migration = "20260925092305_AddS8C1MarketplaceAuthorizationFoundation";

    private PostgreSqlContainer _freshContainer = null!;
    private PostgreSqlContainer _upgradeContainer = null!;
    private string _freshConnectionString = string.Empty;
    private string _upgradeConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        VerceDbContext.ConfigureModuleAssemblies(Verce.Api.ModuleAssemblyCatalog.All);
        _freshContainer = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("verce_s8c1_fresh").WithUsername("verce").WithPassword("verce_test_only").Build();
        _upgradeContainer = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("verce_s8b_to_s8c1").WithUsername("verce").WithPassword("verce_test_only").Build();
        await Task.WhenAll(_freshContainer.StartAsync(), _upgradeContainer.StartAsync());
        _freshConnectionString = _freshContainer.GetConnectionString();
        _upgradeConnectionString = _upgradeContainer.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        await _freshContainer.DisposeAsync();
        await _upgradeContainer.DisposeAsync();
    }

    private VerceDbContext FreshContext() => new(new DbContextOptionsBuilder<VerceDbContext>()
        .UseNpgsql(_freshConnectionString).UseSnakeCaseNamingConvention().Options);
    private VerceDbContext UpgradeContext() => new(new DbContextOptionsBuilder<VerceDbContext>()
        .UseNpgsql(_upgradeConnectionString).UseSnakeCaseNamingConvention().Options);

    /// <summary>Phase 1 (mandate item a): a completely fresh, empty disposable database migrates
    /// zero-to-head cleanly, with the S8C.1 migration itself included and no pending migrations
    /// left afterward.</summary>
    [Fact]
    public async Task Phase1_fresh_database_migrates_zero_to_head_including_S8C1()
    {
        await using var db = FreshContext();
        await db.Database.MigrateAsync();

        var pending = await db.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(m => m.Contains("AddS8C1MarketplaceAuthorizationFoundation"));

        var canConnect = await db.Database.CanConnectAsync();
        canConnect.Should().BeTrue();
    }

    /// <summary>Phases 2-4 (mandate items b/c, d, e): a SEPARATE disposable database is brought to
    /// the real, exact S8B predecessor schema via EF's own migrator (not hand-built partial
    /// tables), seeded with representative valid S8B data across every table/FK relationship the
    /// mandate lists — including one MarketplaceAccount carrying legacy connection_state/
    /// last_error/credential_reference values that are obviously fictitious markers, never real
    /// secrets — then upgraded to S8C.1 alone, verified, rolled back to the exact predecessor, and
    /// reapplied, to catch any non-idempotent assumption in either direction.</summary>
    [Fact]
    public async Task Phases2to4_S8B_predecessor_data_upgrades_to_S8C1_fail_closed_then_downgrades_then_reapplies()
    {
        // ---- Phase 2 setup: bring the database to the REAL S8B predecessor state, no further ----
        await using (var db = UpgradeContext())
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S8BFinalMigration);

        var channel = new SalesChannel("S8C1-UPG-CH", "Canal upgrade S8C.1", SalesChannelKind.Marketplace, .25m, null);
        var product = new Product("S8C1-UPG-PROD", "Produto upgrade S8C.1", null);
        await using (var db = UpgradeContext())
        {
            db.AddRange(channel, product);
            await db.SaveChangesAsync();
        }

        // commerce.* tables changed shape in S8C.1 (marketplace_account lost connection_state/
        // last_error/last_failure_at; marketplace_account_capability gained source/
        // provider_reason_code) — the CURRENT EF model reflects the POST-migration shape, so
        // seeding the PRE-migration (real S8B) shape must go through raw SQL against the actual
        // S8B columns, never the current C# entity, which cannot even express connection_state.
        var legacyAccountId = Guid.NewGuid();
        var channelOfferId = Guid.NewGuid();
        var listingId = Guid.NewGuid();
        var observationId = Guid.NewGuid();
        const string legacyCredentialReference = "LEGACY-FICTITIOUS-CREDENTIAL-REFERENCE-DO-NOT-USE";
        const string legacyLastError = "LEGACY FICTITIOUS ERROR — DO NOT USE — pre-S8C.1 free-text field";
        var now = DateTimeOffset.UtcNow;

        await using (var connection = new NpgsqlConnection(_upgradeConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO commerce.marketplace_provider (code, name)
                VALUES ('SHOPEE', 'Shopee');

                INSERT INTO commerce.marketplace_provider_capability
                    (provider_code, capability_code, state, source, verified_at, updated_at)
                VALUES ('SHOPEE', 'LISTINGS_READ', 'SUPPORTED', 'CATALOG', NULL, @now);

                INSERT INTO commerce.marketplace_account
                    (id, provider_code, external_account_id, sales_channel_id, display_name, active,
                     credential_reference, connection_state, sync_state, last_sync_attempt_at,
                     last_successful_sync_at, last_failure_at, last_error, created_at, version)
                VALUES
                    (@account, 'SHOPEE', 'S8B-LEGACY-EXTERNAL-ID', @channel, 'Loja legada S8B', true,
                     @credRef, 'CONNECTED', 'SYNCED', @now, @now, @now, @lastError, @now, 1);

                INSERT INTO commerce.marketplace_account_capability
                    (marketplace_account_id, capability_code, state, updated_at, verified_at)
                VALUES (@account, 'LISTINGS_READ', 'GRANTED', @now, @now);

                INSERT INTO commerce.channel_offer
                    (id, product_id, sales_channel_id, status, intended_unit_price, price_source,
                     seller_paid_shipping_amount, created_at, version)
                VALUES (@offer, @product, @channel, 'ACTIVE', 49.90, 'MANUAL', NULL, @now, 1);

                INSERT INTO commerce.marketplace_listing
                    (id, marketplace_account_id, external_listing_id, external_sku, product_id,
                     channel_offer_id, title_snapshot, observed_price, observed_status,
                     linkage_state, sync_state, provider_observed_at, created_at, version)
                VALUES
                    (@listing, @account, 'S8B-LEGACY-LISTING-ID', 'SKU-LEGACY', @product, @offer,
                     'Anúncio legado S8B', 49.90, 'ACTIVE', 'LINKED', 'SYNCED', @now, @now, 1);

                INSERT INTO commerce.marketplace_listing_observation
                    (id, marketplace_listing_id, external_sku, title_snapshot, observed_price,
                     observed_status, observation_key, fingerprint, provenance,
                     provider_observed_at, ingested_at, created_at)
                VALUES
                    (@observation, @listing, 'SKU-LEGACY', 'Anúncio legado S8B', 49.90, 'ACTIVE',
                     'initial:S8B-LEGACY-LISTING-ID', 'fictitious-fingerprint-legacy', 'MANUAL',
                     @now, @now, @now);
                """;
            command.Parameters.AddWithValue("account", legacyAccountId);
            command.Parameters.AddWithValue("channel", channel.Id);
            command.Parameters.AddWithValue("product", product.Id);
            command.Parameters.AddWithValue("offer", channelOfferId);
            command.Parameters.AddWithValue("listing", listingId);
            command.Parameters.AddWithValue("observation", observationId);
            command.Parameters.AddWithValue("credRef", legacyCredentialReference);
            command.Parameters.AddWithValue("lastError", legacyLastError);
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync();
        }

        // ---- Phase 3 (mandate item c): apply ONLY S8C.1 on top of the real S8B predecessor ----
        await using (var db = UpgradeContext())
            await db.Database.MigrateAsync();

        await using (var proof = new NpgsqlConnection(_upgradeConnectionString))
        {
            await proof.OpenAsync();

            // Preservation: every listed row/FK relationship survives the upgrade untouched.
            await using (var cmd = new NpgsqlCommand("SELECT name FROM commerce.marketplace_provider WHERE code = 'SHOPEE'", proof))
                (await cmd.ExecuteScalarAsync()).Should().Be("Shopee");
            await using (var cmd = new NpgsqlCommand("SELECT state FROM commerce.marketplace_provider_capability WHERE provider_code = 'SHOPEE' AND capability_code = 'LISTINGS_READ'", proof))
                (await cmd.ExecuteScalarAsync()).Should().Be("SUPPORTED");
            await using (var cmd = new NpgsqlCommand("SELECT external_account_id, display_name, active, sync_state FROM commerce.marketplace_account WHERE id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", legacyAccountId);
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be("S8B-LEGACY-EXTERNAL-ID");
                reader.GetString(1).Should().Be("Loja legada S8B");
                reader.GetBoolean(2).Should().BeTrue();
                reader.GetString(3).Should().Be("SYNCED");
            }
            await using (var cmd = new NpgsqlCommand("SELECT state, source, provider_reason_code FROM commerce.marketplace_account_capability WHERE marketplace_account_id = @id AND capability_code = 'LISTINGS_READ'", proof))
            {
                cmd.Parameters.AddWithValue("id", legacyAccountId);
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be("GRANTED", "no capability may be silently promoted/demoted by the migration");
                // "no capability silently promoted": the new `source` column's own default
                // ('LEGACY_MANUAL') is exactly what a pre-existing, non-S8C.1-issued grant must
                // be labeled — never silently upgraded to a value implying real authorization
                // provenance (e.g. 'AUTH_INSPECTION').
                reader.GetString(1).Should().Be("LEGACY_MANUAL");
                reader.IsDBNull(2).Should().BeTrue();
            }
            await using (var cmd = new NpgsqlCommand("SELECT product_id, sales_channel_id, status, intended_unit_price FROM commerce.channel_offer WHERE id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", channelOfferId);
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetGuid(0).Should().Be(product.Id);
                reader.GetGuid(1).Should().Be(channel.Id);
                reader.GetString(2).Should().Be("ACTIVE");
                reader.GetDecimal(3).Should().Be(49.90m);
            }
            await using (var cmd = new NpgsqlCommand("SELECT marketplace_account_id, channel_offer_id, product_id, linkage_state FROM commerce.marketplace_listing WHERE id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", listingId);
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetGuid(0).Should().Be(legacyAccountId);
                reader.GetGuid(1).Should().Be(channelOfferId);
                reader.GetGuid(2).Should().Be(product.Id);
                reader.GetString(3).Should().Be("LINKED");
            }
            await using (var cmd = new NpgsqlCommand("SELECT marketplace_listing_id, observation_key, fingerprint FROM commerce.marketplace_listing_observation WHERE id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", observationId);
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetGuid(0).Should().Be(listingId);
                reader.GetString(1).Should().Be("initial:S8B-LEGACY-LISTING-ID");
                reader.GetString(2).Should().Be("fictitious-fingerprint-legacy");
            }

            // Fail-closed state: legacy connection_state/last_error/credential_reference are no
            // longer authoritative — the columns themselves are dropped, and a fresh, safe
            // marketplace_account_connection row is the ONLY authority now.
            await using (var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema='commerce' AND table_name='marketplace_account' AND column_name IN ('connection_state','last_error','last_failure_at')", proof))
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeFalse("connection_state/last_error/last_failure_at must be physically dropped from marketplace_account, not merely ignored");
            }
            await using (var cmd = new NpgsqlCommand("SELECT credential_reference FROM commerce.marketplace_account WHERE id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", legacyAccountId);
                (await cmd.ExecuteScalarAsync()).Should().BeOfType<DBNull>("the untrusted legacy credential_reference must never be treated as a valid protected credential after the upgrade");
            }
            await using (var cmd = new NpgsqlCommand(
                "SELECT authorization_state, runtime_availability, confirmed_credential_version, confirmed_operation_id FROM commerce.marketplace_account_connection WHERE marketplace_account_id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", legacyAccountId);
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue("every existing account must get exactly one connection row initialized by the migration");
                reader.GetString(0).Should().Be("NOT_CONNECTED", "no account may silently become CONNECTED across the upgrade, even one whose legacy connection_state was CONNECTED");
                reader.GetString(1).Should().Be("UNKNOWN");
                reader.IsDBNull(2).Should().BeTrue("no credential version may be fabricated for a legacy account with no real protected-store receipt");
                reader.IsDBNull(3).Should().BeTrue("no confirmed operation may be fabricated for a legacy account");
            }
            await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM commerce.marketplace_account_operation WHERE marketplace_account_id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", legacyAccountId);
                Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().Be(0, "the migration must not invent an operation record for a legacy account that never went through the real authorization flow");
            }
            await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM commerce.marketplace_provider WHERE code = 'FAKE'", proof))
                Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().Be(0, "no fake provider row may ever be created by a real migration — ADR-0024 G-06");

            var pendingAfterUp = await UpgradeContext().Database.GetPendingMigrationsAsync();
            pendingAfterUp.Should().BeEmpty();

            // ---- Phase 4 (mandate item d): downgrade to the exact S8B predecessor and document
            // the Down migration's ACTUAL behavior — not a preservation guarantee it never made. ----
            await using (var db = UpgradeContext())
                await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(S8BFinalMigration);

            foreach (var table in new[] { "commerce.marketplace_authorization_session", "commerce.marketplace_account_operation", "commerce.marketplace_account_connection" })
            {
                await using var cmd = new NpgsqlCommand("SELECT to_regclass(@name)::text", proof);
                cmd.Parameters.AddWithValue("name", table);
                (await cmd.ExecuteScalarAsync()).Should().BeOfType<DBNull>($"S8C.1 table {table} must be absent after rollback");
            }
            await using (var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema='commerce' AND table_name='marketplace_account_capability' AND column_name IN ('source','provider_reason_code')", proof))
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeFalse("source/provider_reason_code must be dropped again on rollback");
            }
            // Documented actual Down behavior (ADR-0024 §6, and the migration's own inline
            // comment): connection_state/last_error/last_failure_at come BACK, but Down never
            // promised to RESTORE their original values — Up() already discarded them
            // irrecoverably. Every row, including the previously-CONNECTED legacy one, ends up
            // at the enum's own safe-unknown member (NOT_CONFIGURED), never back at CONNECTED.
            await using (var cmd = new NpgsqlCommand("SELECT connection_state, last_error, last_failure_at, credential_reference FROM commerce.marketplace_account WHERE id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", legacyAccountId);
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be("NOT_CONFIGURED", "Down restores the column but explicitly cannot and does not restore the original CONNECTED value");
                reader.IsDBNull(1).Should().BeTrue("last_error is not and cannot be restored to its original fictitious text");
                reader.IsDBNull(2).Should().BeTrue("last_failure_at is not and cannot be restored");
                reader.IsDBNull(3).Should().BeTrue("credential_reference was already cleared by Up() and Down never promised to restore it");
            }
            // What Down DOES preserve: everything it never touched — the account row itself and
            // every other table's data survive the round trip intact.
            await using (var cmd = new NpgsqlCommand("SELECT display_name, active FROM commerce.marketplace_account WHERE id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", legacyAccountId);
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be("Loja legada S8B");
                reader.GetBoolean(1).Should().BeTrue();
            }
            await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM commerce.marketplace_listing WHERE id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", listingId);
                Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().Be(1);
            }
            await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM commerce.marketplace_listing_observation WHERE id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", observationId);
                Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().Be(1);
            }

            // ---- Phase 5 (mandate item e): reapply S8C.1 on the SAME rolled-back database ----
            await using (var db = UpgradeContext())
                await db.Database.MigrateAsync();

            var pendingAfterReapply = await UpgradeContext().Database.GetPendingMigrationsAsync();
            pendingAfterReapply.Should().BeEmpty("the migration must be cleanly reapplyable after a rollback, not merely appliable once ever");

            await using (var cmd = new NpgsqlCommand(
                "SELECT authorization_state, runtime_availability FROM commerce.marketplace_account_connection WHERE marketplace_account_id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", legacyAccountId);
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue("reapplying Up() must re-seed the connection row exactly as the first application did");
                reader.GetString(0).Should().Be("NOT_CONNECTED");
                reader.GetString(1).Should().Be("UNKNOWN");
            }
            await using (var cmd = new NpgsqlCommand("SELECT credential_reference FROM commerce.marketplace_account WHERE id = @id", proof))
            {
                cmd.Parameters.AddWithValue("id", legacyAccountId);
                (await cmd.ExecuteScalarAsync()).Should().BeOfType<DBNull>();
            }
            await using (var cmd = new NpgsqlCommand("SELECT source FROM commerce.marketplace_account_capability WHERE marketplace_account_id = @id AND capability_code = 'LISTINGS_READ'", proof))
            {
                cmd.Parameters.AddWithValue("id", legacyAccountId);
                (await cmd.ExecuteScalarAsync()).Should().Be("LEGACY_MANUAL");
            }
            await using (var cmd = new NpgsqlCommand(
                "SELECT column_name FROM information_schema.columns WHERE table_schema='commerce' AND table_name='marketplace_account' AND column_name IN ('connection_state','last_error','last_failure_at')", proof))
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeFalse("reapplied Up() must drop the legacy columns again");
            }
        }
    }
}
