using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Verce.IntegrationTests.Auth;
using Verce.IntegrationTests.DataProtection;

namespace Verce.IntegrationTests.S2;

[Collection(PostgresCollection.Name)]
public sealed class BrandAssetProductionStartupTests
{
    private readonly PostgresFixture _fixture;
    public BrandAssetProductionStartupTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Production_without_an_explicit_durable_brand_root_fails_before_serving_HTTP()
    {
        var (certificatePath, passwordPath) = WriteCertificate();
        try
        {
            await using var baseFactory = new VerceWebApplicationFactory(_fixture.ConnectionString,
                new Dictionary<string, string?>
                {
                    ["DataProtection:CurrentCertificatePath"] = certificatePath,
                    ["DataProtection:CertificatePasswordFile"] = passwordPath,
                });
            await using var production = baseFactory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
            var act = () => production.CreateClient();
            act.Should().Throw<InvalidOperationException>().WithMessage("*BrandAssets:StorageRoot*");
        }
        finally
        {
            File.Delete(certificatePath);
            File.Delete(passwordPath);
        }
    }

    [Fact]
    public async Task Production_with_an_absolute_brand_root_starts_and_serves_liveness()
    {
        var (certificatePath, passwordPath) = WriteCertificate();
        var storageRoot = Path.Combine(Path.GetTempPath(), "verce-production-assets-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var baseFactory = new VerceWebApplicationFactory(_fixture.ConnectionString,
                new Dictionary<string, string?>
                {
                    ["DataProtection:CurrentCertificatePath"] = certificatePath,
                    ["DataProtection:CertificatePasswordFile"] = passwordPath,
                    ["BrandAssets:StorageRoot"] = storageRoot,
                });
            await using var production = baseFactory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
            using var client = production.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            (await client.GetAsync("/health/live")).EnsureSuccessStatusCode();
        }
        finally
        {
            File.Delete(certificatePath);
            File.Delete(passwordPath);
            if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
        }
    }

    private static (string CertificatePath, string PasswordPath) WriteCertificate()
    {
        const string password = "temporary-test-password";
        using var certificate = EphemeralCertificateFactory.CreateSelfSigned("verce-brand-storage-production-test");
        var path = Path.Combine(Path.GetTempPath(), "verce-brand-storage-" + Guid.NewGuid().ToString("N") + ".pfx");
        var passwordPath = path + ".password";
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        File.WriteAllText(passwordPath, password);
        return (path, passwordPath);
    }
}
