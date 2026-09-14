using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Settings;

namespace Verce.IntegrationTests.S2;

/// <summary>
/// H-S2-001: the seeded default branding must be real, visible VERCE identity — never the
/// transparent 1×1 placeholder Codex Sol flagged — while remaining idempotent across restarts.
/// </summary>
public sealed partial class S2HttpIntegrationTests
{
    [Fact]
    public async Task Seeded_brand_assets_are_visible_non_placeholder_images_with_distinct_content()
    {
        await using var db = _fixture.CreateContext();
        var versions = await db.Set<BrandAssetVersion>().AsNoTracking().ToListAsync();

        versions.Should().HaveCount(4, "PRIMARY_LOGO, COMPACT_LOGO, SYMBOL and FAVICON are each seeded with one version");
        versions.Should().OnlyContain(v => v.WidthPx > 1 && v.HeightPx > 1, "no seeded asset may be a 1×1 placeholder");
        versions.Should().OnlyContain(v => v.ContentType == "image/png" && v.FileSizeBytes > 100);
        // COMPACT_LOGO and SYMBOL deliberately share one generated mark (content-addressed
        // storage naturally dedupes it) — not a regression to the "four invisible copies"
        // finding, since the wide lockup and the favicon are still visibly distinct images.
        versions.Select(v => v.Sha256).Distinct().Should().HaveCountGreaterThan(1, "at least the wide lockup and the favicon must be visibly different images");

        var favicon = versions.Single(v => v.WidthPx == 64 && v.HeightPx == 64);
        var primary = versions.Single(v => v.WidthPx == 480 && v.HeightPx == 120);
        favicon.Sha256.Should().NotBe(primary.Sha256);
    }

    [Fact]
    public async Task Restarting_the_host_never_duplicates_seeded_brand_assets_or_assignments()
    {
        await using (var before = _fixture.CreateContext())
        {
            (await before.Set<BrandAsset>().CountAsync()).Should().Be(4);
            (await before.Set<BrandAssetVersion>().CountAsync()).Should().Be(4);
            (await before.Set<BrandingAssignment>().CountAsync()).Should().Be(4);
        }

        await _factory.DisposeAsync();
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, new Dictionary<string, string?> { ["Settings:SeedOnStartup"] = "true", ["BrandAssets:StorageRoot"] = _storageRoot });
        using var restart = _factory.CreateHttpsClient();
        (await restart.GetAsync("/health/ready")).EnsureSuccessStatusCode();

        await using var after = _fixture.CreateContext();
        (await after.Set<BrandAsset>().CountAsync()).Should().Be(4);
        (await after.Set<BrandAssetVersion>().CountAsync()).Should().Be(4);
        (await after.Set<BrandingAssignment>().CountAsync()).Should().Be(4);
    }
}
