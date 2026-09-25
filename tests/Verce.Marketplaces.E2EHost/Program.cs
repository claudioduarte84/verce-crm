using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.IntegrationTests.Auth;
using Verce.IntegrationTests.Commerce;
using Verce.Marketplaces.E2EHost;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

// ADR-0024 G-06 / S8C.1 third-round mandate: a separate, test-owned executable that boots the
// REAL Verce.Api application (its actual Program.cs, byte-for-byte unmodified — this project has
// no ProjectReference cycle back into Verce.Api's own composition, it only reuses the compiled
// `Program` type WebApplicationFactory is designed to reuse) on a genuine Kestrel server, with the
// S8C.1 fake marketplace connector wired in ONLY through this host's own external DI substitution
// — exactly the same technique tests/Verce.IntegrationTests/Commerce/MarketplaceAuthorization*.cs
// already uses for the 27 in-process RT-01/02/03 PostgreSQL integration tests, the only difference
// being the transport: those tests use WebApplicationFactory's in-memory TestServer, this host
// uses real TCP sockets so a real Chromium browser driven by Playwright can reach it.
//
// Never started by anything except tests/e2e/playwright.marketplace.config.ts's own webServer
// entry. `dotnet run --project src/Verce.Api` (the command every OTHER Playwright config/spec in
// this repository uses) is completely untouched by this project's existence.

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Verce")
    ?? throw new InvalidOperationException("VERCE_E2E_HOST_CONNECTION_STRING_REQUIRED: set ConnectionStrings__Verce.");

var urls = ParseUrls(args) ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? throw new InvalidOperationException("VERCE_E2E_HOST_URLS_REQUIRED: pass --urls <url> or set ASPNETCORE_URLS.");

using var harness = new MarketplaceTestHarness();

// Restores genuine production/dotnet-run scheduling and seeding defaults — VerceWebApplicationFactory
// defaults these OFF because most in-process xunit tests need to control timing deterministically
// themselves; a real browser-driven E2E host instead needs the same real startup behavior
// `dotnet run --project src/Verce.Api` already exercises for the rest of the E2E suite (real
// seeded settings/products/channels, real outbox/expiration scheduling).
var extraConfiguration = new Dictionary<string, string?>(harness.ExtraConfiguration)
{
    ["Outbox:SchedulingEnabled"] = "true",
    ["Quoting:Expiration:SchedulingEnabled"] = "true",
    ["Settings:SeedOnStartup"] = "true",
};

// The store-loss Playwright journey performs harness-side filesystem fault injection (deleting
// confirmed credential envelopes to simulate total local-store loss) directly from Node — it must
// therefore know this path in advance rather than discover a randomly generated one after the
// fact. playwright.marketplace.config.ts computes and owns this path; absent that (e.g. a manual
// run), MarketplaceTestHarness's own auto-generated isolated temp directory is used instead.
var credentialStoreRootOverride = Environment.GetEnvironmentVariable("VERCE_E2E_MARKETPLACE_CREDENTIAL_STORE_ROOT");
if (!string.IsNullOrWhiteSpace(credentialStoreRootOverride))
    extraConfiguration["Marketplaces:CredentialStoreRoot"] = credentialStoreRootOverride;

await using var factory = new VerceWebApplicationFactory(
    connectionString,
    extraConfiguration,
    configureTestServices: services =>
    {
        harness.ConfigureServices(services);
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(
            sp => new MarketplaceE2EControlEndpointsStartupFilter(sp.GetRequiredService<FakeMarketplaceControlPlane>()));
    },
    useRealKestrelServer: true,
    realKestrelUrls: urls);

// Triggers WebApplicationFactory's lazy EnsureServer()/CreateHost() — see VerceWebApplicationFactory's
// CreateHost override: this is what actually builds and starts the real Kestrel host (and, as part
// of that, awaits every real IHostedService including the genuine CommerceSeedService, so it has
// already finished seeding the real MERCADO_LIVRE/SHOPEE/TIKTOK_SHOP provider catalog rows by the
// time control returns here).
_ = factory.Server;

// ADR-0024 G-06: "Test host registers it through the real registry/ports and inserts test-only
// FAKE provider/capability fixtures into its disposable PostgreSQL. Normal seed has no FAKE row."
// CommerceSeedService (Verce.Api's own real composition) deliberately never seeds this — inserted
// here, by this test-owned host only, so the real frontend's provider dropdown has something to
// select for the fake connector's journeys. Idempotent, since a real browser session may reload
// the providers list more than once.
using (var scope = factory.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
    if (!await db.Set<MarketplaceProvider>().AnyAsync(x => x.Code == FakeMarketplaceConnector.Code))
    {
        db.Add(new MarketplaceProvider(FakeMarketplaceConnector.Code, "Fake E2E Provider"));
        await db.SaveChangesAsync();
    }
}

Console.WriteLine("VERCE_E2E_MARKETPLACE_HOST_READY " + string.Join(',', factory.RealServerAddresses ?? []));
Console.WriteLine("VERCE_E2E_MARKETPLACE_CREDENTIAL_STORE_ROOT " + harness.CredentialStoreRoot);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
try { await Task.Delay(Timeout.Infinite, shutdown.Token); }
catch (OperationCanceledException) { /* normal shutdown */ }

return 0;

static string? ParseUrls(string[] arguments)
{
    for (var i = 0; i < arguments.Length - 1; i++)
        if (arguments[i] == "--urls") return arguments[i + 1];
    return null;
}
