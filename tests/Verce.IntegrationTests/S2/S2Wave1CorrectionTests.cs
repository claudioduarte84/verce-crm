using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Verce.Api.Settings;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Settings;
using Verce.Platform.Audit;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.S2;

public sealed partial class S2HttpIntegrationTests
{
    [Fact]
    public async Task Logout_then_login_in_the_same_client_rotates_antiforgery_without_a_stale_token_failure()
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        AuthTestClient client;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Verce.Platform.Identity.ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Verce.Platform.Identity.ApplicationRole>>();
            if (!await roles.RoleExistsAsync(Roles.Owner)) (await roles.CreateAsync(new Verce.Platform.Identity.ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
            var user = new Verce.Platform.Identity.ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Relogin", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
            (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
            (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();
            client = new AuthTestClient(_factory.CreateHttpsClient());
        }

        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/logout", new { })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new Verce.Api.Auth.LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/auth/session")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Brand_content_is_readable_by_all_authenticated_product_roles_while_admin_stays_owner_only()
    {
        await using var db = _fixture.CreateContext();
        var versionId = await db.Set<BrandAssetVersion>().Select(item => item.Id).FirstAsync();
        var path = $"/api/settings/brand-assets/versions/{versionId}/content";

        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        (await owner.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
        var (operatorClient, _) = await LoggedInAsAsync(Roles.Operator);
        (await operatorClient.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);
        var (viewer, _) = await LoggedInAsAsync(Roles.Viewer);
        (await viewer.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await operatorClient.PostAsync("/api/settings/brand-assets", new BrandAssetCreateRequest("OTHER", "Negado"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsync("/api/settings/brand-assets", new BrandAssetCreateRequest("OTHER", "Negado"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Address_delete_requires_the_observed_customer_version_and_maps_stale_state_to_conflict()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await Json(await owner.PostAsync("/api/customers", Customer("Delete concorrente", "52998224725")));
        var customerId = created.RootElement.GetProperty("id").GetGuid();
        var initialVersion = created.RootElement.GetProperty("version").GetInt64();
        var added = await Json(await owner.PostAsync($"/api/customers/{customerId}/addresses", Address("Principal", true, true, initialVersion)));
        var addressId = added.RootElement.GetProperty("id").GetGuid();
        var beforeMutation = await ReadCustomerAsync(owner, customerId);
        var observedVersion = beforeMutation.RootElement.GetProperty("version").GetInt64();

        (await owner.PutAsync($"/api/customers/{customerId}", Customer("Delete atualizado", "52998224725", observedVersion))).StatusCode.Should().Be(HttpStatusCode.OK);
        var staleDelete = await owner.DeleteAsync($"/api/customers/{customerId}/addresses/{addressId}?customerVersion={observedVersion}");
        staleDelete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCustomerAsync(owner, customerId)).RootElement.GetProperty("addresses").GetArrayLength().Should().Be(1);

        var current = await ReadCustomerAsync(owner, customerId);
        var successfulDelete = await owner.DeleteAsync($"/api/customers/{customerId}/addresses/{addressId}?customerVersion={current.RootElement.GetProperty("version").GetInt64()}");
        successfulDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadCustomerAsync(owner, customerId)).RootElement.GetProperty("addresses").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Address_delete_maps_a_commit_time_EF_concurrency_conflict_to_409()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var created = await Json(await owner.PostAsync("/api/customers", Customer("Delete EF", "52998224725")));
        var customerId = created.RootElement.GetProperty("id").GetGuid();
        var added = await Json(await owner.PostAsync(
            $"/api/customers/{customerId}/addresses",
            Address("Principal", true, true, created.RootElement.GetProperty("version").GetInt64())));
        var addressId = added.RootElement.GetProperty("id").GetGuid();
        var observedVersion = (await ReadCustomerAsync(owner, customerId)).RootElement.GetProperty("version").GetInt64();

        await using var blocker = _fixture.CreateContext();
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM customers.customer WHERE id = {customerId} FOR UPDATE");

        var deleteTask = owner.DeleteAsync(
            $"/api/customers/{customerId}/addresses/{addressId}?customerVersion={observedVersion}");

        await using var monitor = new NpgsqlConnection(_fixture.ConnectionString);
        await monitor.OpenAsync();
        await using var waitCommand = monitor.CreateCommand();
        waitCommand.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE NOT granted)";
        var lockObserved = false;
        for (var attempt = 0; attempt < 250 && !lockObserved; attempt++)
        {
            lockObserved = (bool)(await waitCommand.ExecuteScalarAsync())!;
            if (!lockObserved) await Task.Delay(20);
        }
        lockObserved.Should().BeTrue("the DELETE must reach its blocked EF commit before the competing version update");

        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE customers.customer SET version = version + 1 WHERE id = {customerId}");
        await blockerTransaction.CommitAsync();

        (await deleteTask).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadCustomerAsync(owner, customerId)).RootElement.GetProperty("addresses").GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData("quote.default_validity_days", "0")]
    [InlineData("pricing.default_margin_percent", "1")]
    [InlineData("pricing.price_rounding_policy", "UNKNOWN")]
    [InlineData("costing.default_labor_hourly_rate", "-0.01")]
    [InlineData("costing.default_wastage_rate", "1")]
    [InlineData("energy.overhead_factor", "-1")]
    [InlineData("inventory.filament_price_policy", "WEIGHTED_AVERAGE")]
    [InlineData("ui.default_theme", "unknown")]
    [InlineData("branding.product_name", "")]
    [InlineData("documents.preview_retention_days", "-1")]
    [InlineData("uploads.max_image_bytes", "5242881")]
    public async Task Setting_catalog_rejects_out_of_domain_values_through_the_real_HTTP_path(string key, string invalidValue)
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var list = await Json(await owner.GetAsync("/api/settings"));
        var version = list.RootElement.EnumerateArray().Single(item => item.GetProperty("key").GetString() == key).GetProperty("version").GetInt64();
        (await owner.PutAsync($"/api/settings/{key}", new SettingUpdateRequest(invalidValue, version))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_limit_changes_take_effect_from_AppSetting_without_restart()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var bytes = Image("image/png");
        var assetId = await CreateAssetAsync(owner, "OTHER", "Limite dinâmico");
        await SetUploadLimitAsync(owner, bytes.LongLength - 1);
        (await owner.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(bytes, "too-large.png", "image/png"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await SetUploadLimitAsync(owner, bytes.LongLength);
        (await owner.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(bytes, "within-limit.png", "image/png"))).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Canonical_blob_remains_after_the_promoter_fails_later_than_a_concurrent_commit()
    {
        var (ownerA, _) = await LoggedInAsAsync(Roles.Owner);
        var (ownerB, _) = await LoggedInAsAsync(Roles.Owner);
        var assetA = await CreateAssetAsync(ownerA, "OTHER", "Promotor que falha");
        var assetB = await CreateAssetAsync(ownerB, "OTHER", "Referência concorrente");
        var bytes = Image("image/png", 9);

        // H-S2-001 seeds real, visibly distinct default branding into every fresh storage root
        // (no longer a single shared 1×1 placeholder), so this poll must look for the ONE NEW
        // file this test's own upload produces, not assume the directory starts empty.
        var preExisting = Directory.Exists(_storageRoot)
            ? Directory.EnumerateFiles(_storageRoot, "*.png", SearchOption.AllDirectories).ToHashSet()
            : [];

        await using var blocker = _fixture.CreateContext();
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM settings.brand_asset WHERE id = {assetA} FOR UPDATE");

        var operationA = ownerA.PostMultipartAsync(
            $"/api/settings/brand-assets/{assetA}/versions",
            Form(bytes, "same-a.png", "image/png"));
        string? canonical = null;
        for (var attempt = 0; attempt < 250 && canonical is null; attempt++)
        {
            canonical = Directory.Exists(_storageRoot)
                ? Directory.EnumerateFiles(_storageRoot, "*.png", SearchOption.AllDirectories).SingleOrDefault(file => !preExisting.Contains(file))
                : null;
            if (canonical is null) await Task.Delay(20);
        }
        canonical.Should().NotBeNull("operation A must atomically promote before its database work is released");

        var committed = await ownerB.PostMultipartAsync(
            $"/api/settings/brand-assets/{assetB}/versions",
            Form(bytes, "same-b.png", "image/png"));
        committed.StatusCode.Should().Be(HttpStatusCode.Created);

        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM settings.brand_asset WHERE id = {assetA}");
        await blockerTransaction.CommitAsync();

        (await operationA).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ownerB.GetAsync(committed.Headers.Location!.ToString())).StatusCode.Should().Be(HttpStatusCode.OK);
        File.Exists(canonical!).Should().BeTrue("operation A must not delete canonical content referenced by B");
        Directory.EnumerateFiles(_storageRoot, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_brand_uploads_preserve_unique_numbering_and_a_single_current_version()
    {
        var (ownerA, _) = await LoggedInAsAsync(Roles.Owner);
        var (ownerB, _) = await LoggedInAsAsync(Roles.Owner);
        var assetId = await CreateAssetAsync(ownerA, "OTHER", "Uploads concorrentes");
        var bytes = Image("image/png", 11);

        var responses = await Task.WhenAll(
            ownerA.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(bytes, "a.png", "image/png")),
            ownerB.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(bytes, "b.png", "image/png")));
        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Created);

        await using var verify = _fixture.CreateContext();
        var versions = await verify.Set<BrandAssetVersion>().Where(item => item.BrandAssetId == assetId).OrderBy(item => item.VersionNumber).ToListAsync();
        versions.Select(item => item.VersionNumber).Should().Equal(1, 2);
        versions.Count(item => item.IsCurrent).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_brand_activation_has_one_winner_one_conflict_and_one_current_version()
    {
        var (ownerA, _) = await LoggedInAsAsync(Roles.Owner);
        var assetId = await CreateAssetAsync(ownerA, "OTHER", "Ativação concorrente");
        foreach (var pixel in new byte[] { 1, 2, 3 })
            (await ownerA.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(Image("image/png", pixel), $"v{pixel}.png", "image/png"))).StatusCode.Should().Be(HttpStatusCode.Created);

        Guid[] targets;
        long expectedVersion;
        await using (var db = _fixture.CreateContext())
        {
            var asset = await db.Set<BrandAsset>().Include(item => item.Versions).SingleAsync(item => item.Id == assetId);
            expectedVersion = asset.Version;
            targets = asset.Versions.OrderBy(item => item.VersionNumber).Take(2).Select(item => item.Id).ToArray();
        }
        var (ownerB, _) = await LoggedInAsAsync(Roles.Owner);
        var responses = await Task.WhenAll(
            ownerA.PostAsync($"/api/settings/brand-assets/{assetId}/activate", new BrandActivationRequest(targets[0], expectedVersion)),
            ownerB.PostAsync($"/api/settings/brand-assets/{assetId}/activate", new BrandActivationRequest(targets[1], expectedVersion)));
        responses.Count(response => response.StatusCode == HttpStatusCode.NoContent).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        (await ownerA.PostAsync($"/api/settings/brand-assets/{assetId}/activate", new BrandActivationRequest(targets[0], expectedVersion))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using var verify = _fixture.CreateContext();
        var current = await verify.Set<BrandAssetVersion>().Where(item => item.BrandAssetId == assetId && item.IsCurrent).SingleAsync();
        targets.Should().Contain(current.Id);
    }

    [Fact]
    public async Task Address_and_brand_mutations_write_transactional_safe_audit_metadata()
    {
        var (owner, actorId) = await LoggedInAsAsync(Roles.Owner);
        var created = await Json(await owner.PostAsync("/api/customers", Customer("Audit wave", "52998224725")));
        var customerId = created.RootElement.GetProperty("id").GetGuid();
        var address = await Json(await owner.PostAsync($"/api/customers/{customerId}/addresses", Address("Audit", true, true, created.RootElement.GetProperty("version").GetInt64())));
        var addressId = address.RootElement.GetProperty("id").GetGuid();
        var customer = await ReadCustomerAsync(owner, customerId);
        (await owner.PutAsync($"/api/customers/{customerId}/addresses/{addressId}", Address("Audit atualizado", true, true, customer.RootElement.GetProperty("version").GetInt64()))).StatusCode.Should().Be(HttpStatusCode.OK);
        customer = await ReadCustomerAsync(owner, customerId);
        (await owner.DeleteAsync($"/api/customers/{customerId}/addresses/{addressId}?customerVersion={customer.RootElement.GetProperty("version").GetInt64()}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var assetId = await CreateAssetAsync(owner, "OTHER", "Audit brand");
        var first = await Json(await owner.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(Image("image/png", 21), "audit-1.png", "image/png")));
        await owner.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(Image("image/png", 22), "audit-2.png", "image/png"));
        long assetVersion;
        await using (var db = _fixture.CreateContext()) assetVersion = (await db.Set<BrandAsset>().SingleAsync(item => item.Id == assetId)).Version;
        (await owner.PostAsync($"/api/settings/brand-assets/{assetId}/activate", new BrandActivationRequest(first.RootElement.GetProperty("id").GetGuid(), assetVersion))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var verify = _fixture.CreateContext();
        var addressAudits = await verify.AuditLog.Where(item => item.EntityId == addressId).ToListAsync();
        addressAudits.Select(item => item.Operation).Should().Contain(["ADDED", "MODIFIED", "DELETED"]);
        addressAudits.Should().OnlyContain(item => item.UserId == actorId && item.CorrelationId != Guid.Empty && item.EntityTable == "customer_address");
        var assetVersionIds = await verify.Set<BrandAssetVersion>().Where(item => item.BrandAssetId == assetId).Select(item => item.Id).ToListAsync();
        var brandAudits = await verify.AuditLog.Where(item => item.EntityId == assetId || assetVersionIds.Contains(item.EntityId)).ToListAsync();
        brandAudits.Should().Contain(item => item.EntityId == assetId && item.Operation == "ADDED");
        brandAudits.Should().Contain(item => item.EntityTable == "brand_asset_version" && item.Operation == "ADDED");
        brandAudits.Should().Contain(item => item.EntityId == assetId && item.Operation == "MODIFIED");
        brandAudits.Should().OnlyContain(item => item.UserId == actorId && item.CorrelationId != Guid.Empty);
        brandAudits.Should().OnlyContain(item => !(item.NewValuesJson ?? string.Empty).Contains("iVBOR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Corrected_S2_schema_has_metadata_checks_whatsapp_and_expected_table_count()
    {
        await using var db = _fixture.CreateContext();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('customers','settings') AND table_type='BASE TABLE'),
              (SELECT count(*) FROM information_schema.columns WHERE table_schema IN ('customers','settings') AND table_name <> 'brand_asset_type' AND column_name IN ('created_at','created_by','updated_at','updated_by')),
              (SELECT count(*) FROM information_schema.columns WHERE table_schema='settings' AND table_name='company_profile' AND column_name='whatsapp'),
              (SELECT count(*) FROM pg_constraint WHERE conname IN ('ck_customer_person_type','ck_brand_asset_version_file_size'));
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(8);
        reader.GetInt64(1).Should().Be(28);
        reader.GetInt64(2).Should().Be(1);
        reader.GetInt64(3).Should().Be(2);
        await reader.CloseAsync();

        Func<Task> invalidPersonType = () => db.Database.ExecuteSqlRawAsync("""
            INSERT INTO customers.customer (id, person_type, name, is_active, created_at, version)
            VALUES (gen_random_uuid(), 'INVALID', 'Invalid', true, now(), 1)
            """);
        await invalidPersonType.Should().ThrowAsync<PostgresException>().Where(exception => exception.SqlState == PostgresErrorCodes.CheckViolation);

        var assetId = await db.Set<BrandAsset>().Select(item => item.Id).FirstAsync();
        Func<Task> invalidFileSize = () => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO settings.brand_asset_version
              (id, brand_asset_id, version_number, file_path, sha256, content_type, file_size_bytes, width_px, height_px, original_file_name, uploaded_at, is_current, created_at)
            VALUES
              ({Guid.CreateVersion7()}, {assetId}, 999, 'x/x.png', {new string('0', 64)}, 'image/png', 0, 1, 1, 'x.png', now(), false, now())
            """);
        await invalidFileSize.Should().ThrowAsync<PostgresException>().Where(exception => exception.SqlState == PostgresErrorCodes.CheckViolation);
    }

    private async Task SetUploadLimitAsync(AuthTestClient owner, long value)
    {
        var list = await Json(await owner.GetAsync("/api/settings"));
        var setting = list.RootElement.EnumerateArray().Single(item => item.GetProperty("key").GetString() == "uploads.max_image_bytes");
        var response = await owner.PutAsync("/api/settings/uploads.max_image_bytes", new SettingUpdateRequest(value.ToString(System.Globalization.CultureInfo.InvariantCulture), setting.GetProperty("version").GetInt64()));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
