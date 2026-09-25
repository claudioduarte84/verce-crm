using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Verce.Modules.Commerce;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// Wires the S8C.1 fake marketplace connector and a disposable, outside-the-repo credential
/// store root into a <see cref="Auth.VerceWebApplicationFactory"/> — never the operational
/// filesystem, never a repo-relative path (mission safety rule + ADR-0024 G-02).
/// </summary>
public sealed class MarketplaceTestHarness : IDisposable
{
    public string CredentialStoreRoot { get; }
    public FakeMarketplaceControlPlane ControlPlane { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();
    public IReadOnlyDictionary<string, string?> ExtraConfiguration { get; }

    public MarketplaceTestHarness()
    {
        // Deliberately under the OS temp directory, never under the repository/content root —
        // mirrors LocalProtectedCredentialStore.ValidateRoot's own rejection rule, and keeps this
        // harness far from anything resembling the operational verce-postgres/credential state.
        CredentialStoreRoot = Path.Combine(Path.GetTempPath(), "verce-marketplace-test-" + Guid.NewGuid().ToString("N"));
        ExtraConfiguration = new Dictionary<string, string?>
        {
            ["Marketplaces:CredentialStoreRoot"] = CredentialStoreRoot,
            // The cleanup/startup-recovery sweep is real production code; most tests must control
            // exactly when it runs (mirrors Outbox/Quoting scheduling's own default-off posture)
            // so a live 15-minute trigger never races test assertions. Startup-once recovery still
            // runs (it is an IHostedService, not gated by this flag) — that is intentional: it is
            // exactly the "startup resolution" behavior S8C.1 certifies.
            ["Marketplaces:Cleanup:SchedulingEnabled"] = "false",
        };
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(ControlPlane);
        services.AddSingleton<IMarketplaceAuthorizationConnector, FakeMarketplaceConnector>();
        services.AddSingleton<ILoggerProvider>(Logs);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(CredentialStoreRoot)) Directory.Delete(CredentialStoreRoot, recursive: true); }
        catch { /* best-effort cleanup; the OS temp directory is reclaimed anyway */ }
    }
}
