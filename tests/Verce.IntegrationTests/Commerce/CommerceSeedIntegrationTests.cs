using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Modules.Settings;

namespace Verce.IntegrationTests.Commerce;

[Collection(PostgresCollection.Name)]
public sealed class CommerceSeedIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Fresh_migrated_database_seeds_reference_data_idempotently_without_fake_accounts_or_listings()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "verce-s8b-seed-" + Guid.NewGuid().ToString("N"));
        var configuration = new Dictionary<string, string?>
        {
            ["Settings:SeedOnStartup"] = "true",
            ["BrandAssets:StorageRoot"] = storageRoot
        };

        await StartAndStopHostAsync(configuration);
        await StartAndStopHostAsync(configuration);

        await using var db = fixture.CreateContext();
        (await db.Set<MarketplaceProvider>().CountAsync()).Should().Be(3);
        (await db.Set<MarketplaceProvider>().AnyAsync(x => x.Code == "DIRECT")).Should().BeFalse();
        (await db.Set<MarketplaceProviderCapability>().CountAsync()).Should().Be(24);
        (await db.Set<MarketplaceAccount>().CountAsync()).Should().Be(0);
        (await db.Set<MarketplaceListing>().CountAsync()).Should().Be(0);
        (await db.Set<BrandAssetType>().CountAsync(x => x.Code == "PRODUCT_IMAGE")).Should().Be(1);
        (await db.Set<AppSetting>().CountAsync(x => x.Key == "commerce.listing_observation_retention_days")).Should().Be(1);

        if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
    }

    private async Task StartAndStopHostAsync(IReadOnlyDictionary<string, string?> configuration)
    {
        await using var factory = new VerceWebApplicationFactory(fixture.ConnectionString, configuration);
        using var client = factory.CreateHttpsClient();
        (await client.GetAsync("/health/ready")).EnsureSuccessStatusCode();
    }
}
