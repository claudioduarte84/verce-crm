using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Verce.Infrastructure.Marketplaces;
using Verce.Modules.Commerce;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// ADR-0024 G-06: "Production startup rejects any connector registration marked test-only and any
/// FAKE reference row; no fake code is shipped in its project dependency graph, and fake
/// resolution/start returns unsupported when absent." This exercises the REAL composition
/// function (<see cref="MarketplaceInfrastructureServiceCollectionExtensions.AddVerceMarketplaceInfrastructure"/>,
/// the exact call <c>Program.cs</c> makes) against a Production <see cref="IWebHostEnvironment"/>
/// directly — no HTTP host boot required, since the only thing under test is this one
/// composition-root decision, and a full Production host boot would additionally require
/// Playwright/PDF-renderer prerequisites unrelated to marketplaces. <see cref="Verce.Infrastructure.Marketplaces"/>
/// is never referenced by test-only code in normal composition; this test proves the PRODUCTION
/// branch specifically never wires the fake connector even when one happens to be present in the
/// DI container that composes it (simulating a hypothetical misconfiguration), and that the store
/// is the safe unsupported implementation.
/// </summary>
public sealed class MarketplaceProductionIsolationTests
{
    [Fact]
    public void Production_composition_fails_closed_when_a_test_only_connector_is_present()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Even an explicitly configured root must be ignored outside Development.
            ["Marketplaces:CredentialStoreRoot"] = Path.Combine(Path.GetTempPath(), "verce-should-never-be-used-" + Guid.NewGuid().ToString("N")),
        }).Build();
        var environment = new FakeWebHostEnvironment { EnvironmentName = "Production" };

        // Simulates a hypothetical misconfiguration where a fake connector is somehow present in
        // the container (e.g. a shared test-support assembly accidentally referenced). This must
        // fail composition closed rather than silently building a registry that would expose it —
        // "Production startup rejects any connector registration marked test-only" (ADR-0024 G-06).
        services.AddSingleton<IMarketplaceAuthorizationConnector, FakeMarketplaceConnector>();
        services.AddSingleton(new FakeMarketplaceControlPlane());
        var act = () => services.AddVerceMarketplaceInfrastructure(configuration, environment);
        act.Should().Throw<InvalidOperationException>().WithMessage("*MARKETPLACE_TEST_ONLY_CONNECTOR*");
    }

    [Fact]
    public void Production_registers_no_connectors_in_the_ordinary_no_misconfiguration_case()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var environment = new FakeWebHostEnvironment { EnvironmentName = "Production" };
        services.AddVerceMarketplaceInfrastructure(configuration, environment);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IMarketplaceConnectorRegistry>();
        registry.TryGet(FakeMarketplaceConnector.Code, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Production_credential_store_is_unsupported_regardless_of_configuration()
    {
        var services = new ServiceCollection();
        var storeRoot = Path.Combine(Path.GetTempPath(), "verce-should-never-be-used-" + Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Marketplaces:CredentialStoreRoot"] = storeRoot,
        }).Build();
        var environment = new FakeWebHostEnvironment { EnvironmentName = "Production" };
        services.AddVerceMarketplaceInfrastructure(configuration, environment);

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IProtectedCredentialStore>();
        var read = await store.UseConfirmedAsync(new ProtectedCredentialRead(Guid.NewGuid(), "ANY", "any-reference", 1, Guid.NewGuid()), CancellationToken.None);
        read.Outcome.Should().Be(ProtectedCredentialStoreOutcome.STORE_UNAVAILABLE);
        Directory.Exists(storeRoot).Should().BeFalse("Production must never even create the local credential store root, configured or not");
    }

    /// <summary>
    /// S8C.1 third-round mandate: "verify with a concrete test that Verce.Api's actual
    /// published/normal composition still has zero references to the fake assembly." Extends the
    /// existing runtime-composition guards above with a static, compile-time check: Verce.Api's
    /// OWN compiled assembly (not this test project, not tests/Verce.Marketplaces.E2EHost, which
    /// legitimately references the fake to reuse it for real-browser E2E journeys) must never
    /// declare an assembly reference to Verce.IntegrationTests or Verce.Marketplaces.E2EHost — the
    /// two projects the fake connector and its control-plane/control-endpoint code live in. A
    /// reference here would mean the real, published Verce.Api.dll physically carries the fake
    /// connector's IL, regardless of any runtime environment gate.
    /// </summary>
    [Fact]
    public void Verce_Api_assembly_never_references_any_project_the_fake_connector_lives_in()
    {
        var apiAssembly = typeof(Verce.Api.ModuleAssemblyCatalog).Assembly;
        var referencedAssemblyNames = apiAssembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();

        referencedAssemblyNames.Should().NotContain("Verce.IntegrationTests",
            "the fake connector, its control plane and the E2E-only control endpoints all live there — Verce.Api must never carry a reference to it");
        referencedAssemblyNames.Should().NotContain("Verce.Marketplaces.E2EHost",
            "the browser-driven E2E host reuses the fake connector deliberately — Verce.Api must never reference it back");

        // Defense in depth against a hypothetical future refactor that moves the fake connector
        // type somewhere else while still, wrongly, being reachable from Verce.Api: no type named
        // exactly "FakeMarketplaceConnector" may be loadable from Verce.Api's own transitive
        // reference closure at all.
        var transitivelyLoadedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => referencedAssemblyNames.Contains(a.GetName().Name))
            .ToArray();
        foreach (var assembly in transitivelyLoadedAssemblies)
            assembly.GetTypes().Should().NotContain(t => t.Name == "FakeMarketplaceConnector",
                $"assembly {assembly.GetName().Name}, transitively referenced by Verce.Api, must never carry the fake connector type");
    }

    [Fact]
    public void Development_without_an_explicit_credential_store_root_also_stays_unsupported()
    {
        // The local store is opt-in even in Development — "Production S8C.1 has no registered
        // real/fake provider and no local-store activation" is the default posture; a Development
        // host that never sets Marketplaces:CredentialStoreRoot must behave the same way.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var environment = new FakeWebHostEnvironment { EnvironmentName = "Development" };
        services.AddVerceMarketplaceInfrastructure(configuration, environment);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IMarketplaceConnectorRegistry>();
        registry.TryGet(FakeMarketplaceConnector.Code, out _).Should().BeFalse();
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Verce.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Production";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
    }
}
