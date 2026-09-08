using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.DataProtection;

/// <summary>
/// Ephemeral, disposable X.509 certificates for E-series tests (mission §9): generated fresh
/// per test, held only in memory/temp files, NEVER committed. No production certificate is ever
/// touched by these tests.
/// </summary>
public static class EphemeralCertificateFactory
{
    public static X509Certificate2 CreateSelfSigned(string subjectName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));

        // Re-import as Exportable/EphemeralKeySet so the private key survives being handed
        // around as an in-memory X509Certificate2 across the lifetime of one test.
        var pfxBytes = certificate.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.Exportable);
    }
}

/// <summary>Minimal fake used ONLY to select the Development/Production branch inside
/// PlatformServiceCollectionExtensions.AddDataProtection — no other member is ever read.</summary>
public sealed class FakeHostEnvironment : IHostEnvironment
{
    public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;
    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "Verce.IntegrationTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>
/// Builds a Data Protection stack against a REAL PostgreSQL-backed key store
/// (<see cref="VerceDbContext"/>, exactly like production) with an explicit current certificate
/// and unprotect ring — the actual framework behavior, never a boolean mock restating
/// configuration (mission §9).
/// </summary>
public static class DataProtectionTestHost
{
    public static ServiceProvider Build(string connectionString, X509Certificate2 currentCertificate, params X509Certificate2[] additionalUnprotectCertificates)
    {
        var services = new ServiceCollection();
        services.AddDbContext<VerceDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        var builder = services.AddDataProtection()
            .SetApplicationName("Verce3D.Tests")
            .PersistKeysToDbContext<VerceDbContext>();

        builder.ProtectKeysWithCertificate(currentCertificate);

        var ring = new List<X509Certificate2> { currentCertificate };
        ring.AddRange(additionalUnprotectCertificates);
        builder.UnprotectKeysWithAnyCertificate(ring.ToArray());

        return services.BuildServiceProvider();
    }

    public static IDataProtectionProvider Provider(ServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<IDataProtectionProvider>();

    public static IKeyManager KeyManager(ServiceProvider serviceProvider) =>
        serviceProvider.GetRequiredService<IKeyManager>();
}
