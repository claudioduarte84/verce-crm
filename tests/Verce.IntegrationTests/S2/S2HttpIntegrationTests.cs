using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Verce.Api.Auth;
using Verce.Api.Customers;
using Verce.Api.Settings;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Customers;
using Verce.Modules.Settings;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.S2;

/// <summary>Real-host S2 acceptance tests: PostgreSQL, the production authorization pipeline,
/// antiforgery, audit interceptor and filesystem-backed brand storage are all exercised together.</summary>
[Collection(PostgresCollection.Name)]
public sealed partial class S2HttpIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private string _storageRoot = string.Empty;

    public S2HttpIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE settings.branding_assignment, settings.brand_asset_version, settings.brand_asset,
                           settings.brand_asset_type, settings.app_setting, settings.company_profile,
                           customers.customer_address, customers.customer,
                           platform.account_setup_token, platform.user_role, platform.user_claim,
                           platform.user_login, platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
        _storageRoot = Path.Combine(Path.GetTempPath(), "verce-s2-test-assets-" + Guid.NewGuid().ToString("N"));
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Settings:SeedOnStartup"] = "true",
            ["BrandAssets:StorageRoot"] = _storageRoot,
        });
        using var warmup = _factory.CreateHttpsClient();
        (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    [Fact]
    public void Child_uuid_keys_are_explicitly_application_assigned_in_the_ef_model()
    {
        using var db = _fixture.CreateContext();
        db.Model.FindEntityType(typeof(CustomerAddress))!.FindProperty(nameof(CustomerAddress.Id))!.ValueGenerated.Should().Be(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never);
        db.Model.FindEntityType(typeof(BrandAssetVersion))!.FindProperty(nameof(BrandAssetVersion.Id))!.ValueGenerated.Should().Be(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true);
    }

    [Fact]
    public async Task Customer_lifecycle_search_address_invariant_soft_delete_and_audit_are_real()
    {
        var (client, ownerId) = await LoggedInAsAsync(Roles.Owner);
        var create = await client.PostAsync("/api/customers", Customer("Ana de Teste", "529.982.247-25"));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await Json(create);
        var customerId = created.RootElement.GetProperty("id").GetGuid();
        var version = created.RootElement.GetProperty("version").GetInt64();
        var addressA = await client.PostAsync($"/api/customers/{customerId}/addresses", Address("Ateliê", true, true, version));
        addressA.StatusCode.Should().Be(HttpStatusCode.Created, await addressA.Content.ReadAsStringAsync());
        var afterA = await ReadCustomerAsync(client, customerId);
        var addressB = await client.PostAsync($"/api/customers/{customerId}/addresses", Address("Entrega", true, true, afterA.RootElement.GetProperty("version").GetInt64()));
        addressB.StatusCode.Should().Be(HttpStatusCode.Created);

        var customer = await ReadCustomerAsync(client, customerId);
        customer.RootElement.GetProperty("addresses").EnumerateArray().Count(x => x.GetProperty("isPrimary").GetBoolean()).Should().Be(1);
        customer.RootElement.GetProperty("addresses").EnumerateArray().Count(x => x.GetProperty("isDefaultShipping").GetBoolean()).Should().Be(1);
        var update = await client.PutAsync($"/api/customers/{customerId}", Customer("Ana Atualizada", "52998224725", customer.RootElement.GetProperty("version").GetInt64()));
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var search = await client.GetAsync("/api/customers?search=Atualizada&page=1&pageSize=1");
        var found = await Json(search);
        found.RootElement.GetProperty("total").GetInt32().Should().Be(1);
        found.RootElement.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Ana Atualizada");

        var updated = await Json(update);
        (await client.DeleteAsync($"/api/customers/{customerId}?version={updated.RootElement.GetProperty("version").GetInt64()}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync($"/api/customers/{customerId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var empty = await Json(await client.GetAsync("/api/customers?search=Atualizada&page=1&pageSize=10"));
        empty.RootElement.GetProperty("total").GetInt32().Should().Be(0);

        await using var verify = _fixture.CreateContext();
        var persisted = await verify.Set<Customer>().IgnoreQueryFilters().SingleAsync(x => x.Id == customerId);
        persisted.DeletedAt.Should().NotBeNull(); persisted.Document.Should().BeNull();
        (await verify.AuditLog.Where(x => x.EntityId == customerId).Select(x => x.Operation).ToListAsync()).Should().Contain(["ADDED", "MODIFIED"]);
        (await verify.AuditLog.Where(x => x.EntityId == customerId).Select(x => x.UserId).Distinct().ToListAsync()).Should().Contain(ownerId);
    }

    [Fact]
    public async Task Customer_concurrency_duplicate_document_security_and_csrf_are_enforced()
    {
        using var anonymous = _factory.CreateHttpsClient();
        (await anonymous.GetAsync("/api/customers?page=1&pageSize=1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var (viewer, _) = await LoggedInAsAsync(Roles.Viewer);
        (await viewer.PostAsync("/api/customers", Customer("Bloqueado", "52998224725"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        (await owner.PostAsync("/api/customers", Customer("Sem CSRF", "52998224725"), withAntiforgery: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var create = await owner.PostAsync("/api/customers", Customer("Concorrência", "52998224725"));
        var original = await Json(create); var id = original.RootElement.GetProperty("id").GetGuid(); var version = original.RootElement.GetProperty("version").GetInt64();
        (await owner.PutAsync($"/api/customers/{id}", Customer("Primeira alteração", "52998224725", version))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PutAsync($"/api/customers/{id}", Customer("Alteração obsoleta", "52998224725", version))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var writerA = owner;
        var (writerB, _) = await LoggedInAsAsync(Roles.Operator);
        var competing = await Task.WhenAll(
            writerA.PostAsync("/api/customers", Customer("Duplicado A", "73.894.567/0001-22", personType: PersonType.Company)),
            writerB.PostAsync("/api/customers", Customer("Duplicado B", "73894567000122", personType: PersonType.Company)));
        competing.Count(x => x.StatusCode == HttpStatusCode.Created).Should().Be(1);
        competing.Count(x => x.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        await using var verify = _fixture.CreateContext();
        (await verify.Set<Customer>().CountAsync(x => x.Document == "73894567000122")).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(SeedSettings))]
    public async Task Every_seeded_S2_setting_can_be_read_and_valid_values_persist(string key, string value)
    {
        var (owner, ownerId) = await LoggedInAsAsync(Roles.Owner);
        var list = await Json(await owner.GetAsync("/api/settings"));
        var setting = list.RootElement.EnumerateArray().Single(x => x.GetProperty("key").GetString() == key);
        var update = await owner.PutAsync($"/api/settings/{key}", new SettingUpdateRequest(value, setting.GetProperty("version").GetInt64()));
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using var verify = _fixture.CreateContext();
        var persisted = await verify.Set<AppSetting>().SingleAsync(x => x.Key == key);
        persisted.Value.Should().Be(value); persisted.UpdatedBy.Should().Be(ownerId);
        (await verify.AuditLog.AnyAsync(x => x.EntityId == persisted.Id && x.Operation == "MODIFIED")).Should().BeTrue();
    }

    [Fact]
    public async Task Settings_company_profile_and_owner_controls_enforce_validation_concurrency_csrf_and_audit()
    {
        var (owner, ownerId) = await LoggedInAsAsync(Roles.Owner);
        var profile = await Json(await owner.GetAsync("/api/settings/company-profile"));
        var request = Profile("VERCE 3D Atualizada", profile.RootElement.GetProperty("version").GetInt64());
        (await owner.PutAsync("/api/settings/company-profile", request)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.PutAsync("/api/settings/company-profile", request)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await owner.PutAsync("/api/settings/company-profile", Profile("", 2))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PutAsync("/api/settings/company-profile", Profile("Sem csrf", 2), withAntiforgery: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var (operatorClient, _) = await LoggedInAsAsync(Roles.Operator);
        (await operatorClient.GetAsync("/api/settings/company-profile")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using var verify = _fixture.CreateContext();
        var persisted = await verify.Set<CompanyProfile>().SingleAsync();
        persisted.Id.Should().Be(CompanyProfile.SingletonId); persisted.LegalName.Should().Be("VERCE 3D Atualizada");
        (await verify.AuditLog.AnyAsync(x => x.EntityId == persisted.Id && x.UserId == ownerId && x.Operation == "MODIFIED")).Should().BeTrue();
    }

    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/webp", "webp")]
    public async Task Brand_upload_normalizes_each_approved_format_and_serves_safe_content(string contentType, string extension)
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var assetId = await CreateAssetAsync(owner, "OTHER", "Asset " + extension);
        var upload = await owner.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(Image(contentType), "../../malicious." + extension, contentType));
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var location = upload.Headers.Location!.ToString();
        var content = await owner.GetAsync(location);
        content.StatusCode.Should().Be(HttpStatusCode.OK); content.Content.Headers.ContentType!.MediaType.Should().Be(contentType);
        content.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        await using var verify = _fixture.CreateContext();
        var version = await verify.Set<BrandAssetVersion>().SingleAsync(x => x.BrandAssetId == assetId);
        version.OriginalFileName.Should().Be("malicious." + extension); version.FilePath.Should().NotContain("..");
        Path.GetFullPath(Path.Combine(_storageRoot, version.FilePath)).StartsWith(Path.GetFullPath(_storageRoot), StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        version.Sha256.Should().HaveLength(64);
    }

    [Fact]
    public async Task Brand_rejects_spoofed_or_undecodable_images_and_preserves_immutable_versions()
    {
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        var assetId = await CreateAssetAsync(owner, "OTHER", "Versões");
        (await owner.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form([1, 2, 3], "logo.png", "image/png"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var fakePng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0 };
        (await owner.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(fakePng, "logo.png", "image/png"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(Image("image/png"), "logo.jpg", "image/jpeg"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var firstVersion = await owner.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(Image("image/png", 1), "v1.png", "image/png"));
        firstVersion.StatusCode.Should().Be(HttpStatusCode.Created, await firstVersion.Content.ReadAsStringAsync());
        await using var before = _fixture.CreateContext();
        var v1 = await before.Set<BrandAssetVersion>().SingleAsync(x => x.BrandAssetId == assetId);
        var v1Hash = v1.Sha256;
        (await owner.PostMultipartAsync($"/api/settings/brand-assets/{assetId}/versions", Form(Image("image/png", 2), "v2.png", "image/png"))).StatusCode.Should().Be(HttpStatusCode.Created);
        await using var after = _fixture.CreateContext();
        var versions = await after.Set<BrandAssetVersion>().Where(x => x.BrandAssetId == assetId).OrderBy(x => x.VersionNumber).ToListAsync();
        versions.Select(x => x.VersionNumber).Should().Equal(1, 2); versions[0].Sha256.Should().Be(v1Hash); versions[0].IsCurrent.Should().BeFalse(); versions[1].IsCurrent.Should().BeTrue();
        var asset = await after.Set<BrandAsset>().SingleAsync(x => x.Id == assetId); asset.CurrentVersionId.Should().Be(versions[1].Id);
    }

    [Fact]
    public async Task Seed_is_idempotent_and_brand_administration_requires_owner_and_csrf()
    {
        await using (var initial = _fixture.CreateContext())
        {
            (await initial.Set<BrandAsset>().CountAsync()).Should().Be(4);
            (await initial.Set<BrandAssetVersion>().CountAsync()).Should().Be(4);
            (await initial.Set<BrandingAssignment>().CountAsync()).Should().Be(4);
        }
        var (operatorClient, _) = await LoggedInAsAsync(Roles.Operator);
        (await operatorClient.GetAsync("/api/settings/brand-assets")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var (owner, _) = await LoggedInAsAsync(Roles.Owner);
        (await owner.PostAsync("/api/settings/brand-assets", new BrandAssetCreateRequest("OTHER", "Sem csrf"), withAntiforgery: false)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _factory.DisposeAsync();
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, new Dictionary<string, string?> { ["Settings:SeedOnStartup"] = "true", ["BrandAssets:StorageRoot"] = _storageRoot });
        using var restart = _factory.CreateHttpsClient();
        (await restart.GetAsync("/health/ready")).EnsureSuccessStatusCode();
        await using var verify = _fixture.CreateContext();
        (await verify.Set<BrandAsset>().CountAsync()).Should().Be(4); (await verify.Set<BrandAssetVersion>().CountAsync()).Should().Be(4); (await verify.Set<BrandingAssignment>().CountAsync()).Should().Be(4);
    }

    public static IEnumerable<object[]> SeedSettings() => SettingSeeds.All.Select(x => new object[] { x.Key, ValidValue(x.Key, x.Type) });
    private static string ValidValue(string key, AppSettingValueType type) => key switch
    {
        "pricing.default_margin_percent" => "0.42",
        "pricing.price_rounding_policy" => "TEN_CENTS",
        "pricing.margin_warning_denominator" => "0.15",
        "costing.default_wastage_rate" => "0.05",
        "inventory.filament_price_policy" => "MANUAL",
        "ui.default_theme" => "verce-default",
        _ => type switch { AppSettingValueType.Int => "31", AppSettingValueType.Decimal => "1.25", AppSettingValueType.Bool => "false", AppSettingValueType.Json => "{\"valid\":true}", _ => "S2-test-value" },
    };
    private static CustomerRequest Customer(string name, string document, long version = 0, PersonType personType = PersonType.Individual) => new(personType, name, null, document, name.Replace(' ', '.').ToLowerInvariant() + "@example.test", null, null, version);
    private static AddressRequest Address(string label, bool primary, bool shipping, long version) => new(label, "01001-000", "Rua S2", "10", null, "Centro", "São Paulo", "SP", "BR", primary, shipping, null, version);
    private static CompanyProfileRequest Profile(string legalName, long version) => new(legalName, "VERCE", null, null, null, null, null, null, null, null, null, null, null, "São Paulo", "SP", "BR", "America/Sao_Paulo", "BRL", version);
    private async Task<JsonDocument> ReadCustomerAsync(AuthTestClient client, Guid id) { var response = await client.GetAsync($"/api/customers/{id}"); response.StatusCode.Should().Be(HttpStatusCode.OK); return await Json(response); }
    private async Task<Guid> CreateAssetAsync(AuthTestClient client, string type, string name) { var response = await client.PostAsync("/api/settings/brand-assets", new BrandAssetCreateRequest(type, name)); response.StatusCode.Should().Be(HttpStatusCode.Created); return (await Json(response)).RootElement.GetProperty("id").GetGuid(); }
    private async Task<(AuthTestClient Client, Guid UserId)> LoggedInAsAsync(string role)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test"; const string password = "a-perfectly-fine-12char-password";
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(); var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (!await roles.RoleExistsAsync(role)) (await roles.CreateAsync(new ApplicationRole(role))).Succeeded.Should().BeTrue();
            var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = role + " S2", IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow };
            (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue(); (await users.AddToRoleAsync(user, role)).Succeeded.Should().BeTrue();
            var client = new AuthTestClient(_factory.CreateHttpsClient()); await client.EnsureCsrfCookieAsync(); (await client.PostAsync("/api/auth/login", new LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent); await client.EnsureCsrfCookieAsync(); return (client, user.Id);
        }
    }
    private static async Task<JsonDocument> Json(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    private static MultipartFormDataContent Form(byte[] bytes, string name, string contentType)
    {
        var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var form = new MultipartFormDataContent(); form.Add(content, "file", name); return form;
    }
    private static byte[] Image(string contentType, byte pixel = 0)
    {
        using var image = new Image<Rgba32>(1, 1, new Rgba32(pixel, 0, 0)); using var stream = new MemoryStream();
        switch (contentType) { case "image/png": image.Save(stream, new PngEncoder()); break; case "image/jpeg": image.Save(stream, new JpegEncoder()); break; case "image/webp": image.Save(stream, new WebpEncoder()); break; default: throw new ArgumentOutOfRangeException(nameof(contentType)); }
        return stream.ToArray();
    }
}
