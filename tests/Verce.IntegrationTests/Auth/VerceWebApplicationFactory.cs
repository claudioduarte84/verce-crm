using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Auth;

/// <summary>
/// Boots the REAL Verce.Api host (Program.cs, unmodified) against the Testcontainers Postgres
/// database, in the Development profile so Data Protection uses its file-system fallback
/// without requiring a certificate (E-9) — exactly the same composition root S1 ships, not a
/// test-only rewiring of authentication/authorization.
/// </summary>
public sealed class VerceWebApplicationFactory : WebApplicationFactory<Program>
{
    // M-TESTHOST-001: some platform startup code (AddVerceOutboxScheduling) reads configuration
    // EAGERLY, as part of AddVercePlatform's synchronous registration call in Program.cs — which
    // runs BEFORE builder.Build(). WebApplicationFactory's ConfigureWebHost/ConfigureAppConfiguration
    // override below is only merged in once Program.cs itself calls builder.Build() (via the
    // DeferredHostBuilder mechanism), so it arrives too late for that eager read; an eager reader
    // would see the pre-override configuration instead. Environment variables ARE read eagerly by
    // WebApplicationBuilder.CreateBuilder(args) itself, ahead of any of Program.cs's own code, so
    // mirroring settings there makes them visible in time — there is no clean way to remove this
    // without an elaborate deferred-registration rework of Quartz's own fluent configuration API,
    // which reads its connection string and job/trigger cadences just as eagerly (Option A in the
    // mission was evaluated and rejected as disproportionate; this is Option B, implemented with
    // the required rigor below).
    //
    // Because environment variables are process-wide, EVERY factory in the process serializes on
    // this single lock for its ENTIRE lifetime (acquired here in the constructor, released only in
    // DisposeAsync) — not just for the moment it mutates variables. That is what makes "two factory
    // instances cannot overlap environment mutation" true regardless of xUnit scheduling, rather
    // than merely relying on tests usually running sequentially. SemaphoreSlim is deliberately used
    // instead of a lock/Monitor: release is not thread-affine, so holding it across the awaited
    // lifetime of an async test (whose continuations may resume on a different thread) is safe.
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

    // The environment value each key held the FIRST time any factory in this process touched it —
    // never merely "null" — so a value a developer's shell or CI legitimately set survives every
    // factory's construction and disposal. Populated at most once per key, on first touch.
    private static readonly Dictionary<string, string?> OriginalEnvironmentValues = new();
    private static readonly HashSet<string> KeysEverSet = new();

    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, string?> _extraConfiguration;
    private readonly Dictionary<string, string?> _settings;
    private readonly IClock? _clockOverride;
    private readonly Action<IServiceCollection>? _configureTestServices;
    private readonly bool _useRealKestrelServer;
    private readonly string? _realKestrelUrls;
    private bool _environmentLockHeld;
    private IHost? _realKestrelHost;

    /// <summary>Only set when this factory was constructed with <c>useRealKestrelServer: true</c>
    /// (the browser-driven E2E host — <c>tests/Verce.Marketplaces.E2EHost</c> — never in-process
    /// PostgreSQL integration tests) — the real bound address(es) of the genuine Kestrel server a
    /// real browser can reach over real sockets, as opposed to <see cref="WebApplicationFactory{TEntryPoint}.Server"/>'s
    /// in-memory <c>TestServer</c> transport every other caller of this factory keeps using.</summary>
    public IReadOnlyList<string>? RealServerAddresses { get; private set; }

    /// <param name="connectionString">The real PostgreSQL connection string.</param>
    /// <param name="extraConfiguration">Additional configuration overrides — e.g. a host-execution
    /// test (mission §19-23) passes <c>Outbox:SchedulingEnabled=true</c> plus fast test cadences.
    /// Every OTHER test in this project manipulates outbox rows directly and must never race a
    /// live scheduler, so the default (no override) is scheduling DISABLED here — production's
    /// own composition root leaves it enabled; only this test factory opts out by default.</param>
    /// <param name="clockOverride">Terra B-02: when supplied, replaces the real <see cref="SystemClock"/>
    /// with this exact <see cref="IClock"/> for the whole host — used to prove FeeRuleVersion
    /// resolution uses <see cref="IClock.OrganizationToday"/> (America/Sao_Paulo), not
    /// <c>UtcNow.Date</c>, at a real UTC/BRT calendar boundary. Null (the default) leaves
    /// production's real wall clock untouched for every other test.</param>
    /// <param name="useRealKestrelServer">S8C.1 third-round mandate: when true, this factory binds
    /// a REAL Kestrel server (genuine TCP sockets) instead of the in-memory <c>TestServer</c> every
    /// other caller uses, so a real browser driven by Playwright can reach it over real HTTP. Only
    /// <c>tests/Verce.Marketplaces.E2EHost</c> (a separate, test-owned executable — never
    /// <c>Verce.Api</c>'s own composition) sets this; every one of the 461 existing PostgreSQL
    /// integration tests leaves it false and is completely unaffected.</param>
    /// <param name="realKestrelUrls">Semicolon-separated URLs Kestrel binds to when
    /// <paramref name="useRealKestrelServer"/> is true (e.g. "https://localhost:7246"). Ignored
    /// otherwise. Null lets Kestrel pick its own default/dynamic address.</param>
    public VerceWebApplicationFactory(string connectionString, IReadOnlyDictionary<string, string?>? extraConfiguration = null, IClock? clockOverride = null, Action<IServiceCollection>? configureTestServices = null, bool useRealKestrelServer = false, string? realKestrelUrls = null)
    {
        _connectionString = connectionString;
        _extraConfiguration = extraConfiguration ?? new Dictionary<string, string?>();
        _clockOverride = clockOverride;
        _configureTestServices = configureTestServices;
        _useRealKestrelServer = useRealKestrelServer;
        _realKestrelUrls = realKestrelUrls;

        _settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Verce"] = _connectionString,
            ["DataProtection:DevKeyDirectory"] = Path.Combine(Path.GetTempPath(), "verce-test-dp-" + Guid.NewGuid().ToString("N")),
            ["Outbox:SchedulingEnabled"] = "false",
            // S6: ExpireQuotesJob is read the same eager way (AddVerceQuotingScheduling runs
            // right after AddVercePlatform in Program.cs, before builder.Build()) — mirrors
            // Outbox:SchedulingEnabled's default-off posture for the same reason: most tests in
            // this project manipulate quote/revision status directly and must never race a live
            // expiration sweep.
            ["Quoting:Expiration:SchedulingEnabled"] = "false",
            ["Settings:SeedOnStartup"] = "false",
        };
        foreach (var (key, value) in _extraConfiguration) _settings[key] = value;

        EnvironmentLock.Wait();
        _environmentLockHeld = true;
        try
        {
            // Defense in depth (should already be a no-op — the previous holder's DisposeAsync
            // restores originals before releasing this same lock): put every key ANY factory has
            // ever touched back to its true original before applying this instance's own settings,
            // so a key this instance does NOT override can never observe a PREVIOUS instance's
            // leftover value.
            RestoreAllEverSetKeysToOriginal();

            foreach (var (key, value) in _settings)
            {
                var envKey = key.Replace(":", "__");
                if (KeysEverSet.Add(envKey))
                    OriginalEnvironmentValues[envKey] = Environment.GetEnvironmentVariable(envKey);
                Environment.SetEnvironmentVariable(envKey, value);
            }

            // Quartz.Logging.LogProvider caches its resolved provider (wrapping whichever
            // ILoggerFactory was live at first use) in a process-wide static, set directly by
            // Quartz.Extensions.Hosting rather than gated by LogProvider.IsDisabled. Production
            // runs exactly one long-lived host per process, so this is invisible there; this
            // factory boots a fresh host per test, and once one Quartz-enabled host disposes its
            // ILoggerFactory, the NEXT host's first Quartz log call would throw
            // ObjectDisposedException against the PREVIOUS host's already-disposed factory.
            // Resetting it here — guarded by the SAME lock that guarantees no other factory's
            // host is live right now — forces a fresh resolution against THIS host's own
            // (currently alive) ILoggerFactory. Test-only: this type never ships in Verce.Api.
            Quartz.Logging.LogProvider.SetCurrentLogProvider(null!);
        }
        catch
        {
            _environmentLockHeld = false;
            EnvironmentLock.Release();
            throw;
        }
    }

    private static void RestoreAllEverSetKeysToOriginal()
    {
        foreach (var envKey in KeysEverSet)
            Environment.SetEnvironmentVariable(envKey, OriginalEnvironmentValues.GetValueOrDefault(envKey));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));

        // Real-Kestrel mode only (tests/Verce.Marketplaces.E2EHost): WebApplicationFactory's
        // default content-root discovery walks up from the CALLING assembly's own directory
        // looking for a sibling matching the entry-point assembly's simple name ("Verce.Api"),
        // which only happens to resolve correctly for callers that sit at tests/<Project>/ two
        // levels under the repository root, like Verce.IntegrationTests. A different caller
        // directory layout resolves the wrong path entirely. Verce.Api's own content files
        // (appsettings*.json) are already copied transitively into THIS process's own output
        // directory via the ProjectReference chain (proven by inspection, not assumed), so using
        // this process's own base directory as the content root is exact, not a guess.
        if (_useRealKestrelServer) builder.UseContentRoot(AppContext.BaseDirectory);

        // B2 (S2 final blockers): the generic host's default logging (Host.CreateDefaultBuilder)
        // adds the Windows EventLog provider automatically on Windows. EventLogLoggerProvider
        // wraps a per-machine/per-source OS handle (System.Diagnostics.EventLogInternal) that is
        // a PROCESS-WIDE static, not scoped per host. Production never notices — it runs exactly
        // one host for the process's entire lifetime — but this factory boots a fresh host per
        // test: once one host's EventLog provider disposes that shared handle, any other host
        // still writing through it (including THIS SAME host, mid-Quartz-shutdown, logging
        // through Quartz's own MicrosoftLoggingProvider) throws ObjectDisposedException from
        // Host.StopAsync's own hosted-service loop, surfacing as an AggregateException out of
        // WebApplicationFactory.DisposeAsync. Removed here only — Program.cs is untouched
        // because the defect cannot occur in a single-host-per-process production run.
        builder.ConfigureLogging(logging => RemoveEventLogProvider(logging.Services));

        // D-11 needs SecurityStamp revalidation to happen on every request, not on the
        // framework's default 30-minute cadence, so the test can observe session invalidation
        // deterministically without waiting in real time.
        builder.ConfigureServices(services =>
        {
            services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
            if (_clockOverride is not null) services.Replace(ServiceDescriptor.Singleton(_clockOverride));
            _configureTestServices?.Invoke(services);
        });
    }

    /// <summary>
    /// S8C.1 third round: builds a genuine Kestrel-bound host, reachable by real browser/real HTTP
    /// (Playwright), instead of the in-memory <c>TestServer</c> transport <see cref="WebApplicationFactory{TEntryPoint}"/>
    /// uses by default — <c>Verce.Api</c>'s own composition (Program.cs) is completely untouched
    /// either way; only which server implementation THIS test-owned factory hands it differs.
    ///
    /// The base framework's own <c>EnsureServer()</c>/<c>CreateClient()</c> machinery is typed
    /// against <c>TestServer</c>, so a throwaway <c>TestServer</c>-backed host is still built and
    /// started (from the SAME <paramref name="builder"/>, before any Kestrel-specific
    /// configuration is layered on) purely to satisfy that internal contract — it never serves any
    /// real request. The REAL Kestrel host is built and started separately, immediately after, and
    /// its bound address(es) are captured in <see cref="RealServerAddresses"/> for the caller
    /// (<c>tests/Verce.Marketplaces.E2EHost</c>'s own Program.cs) to hand to the browser/Playwright.
    /// This is the standard, widely-documented "real HTTP server via WebApplicationFactory"
    /// technique (e.g. Microsoft.AspNetCore.Mvc.Testing samples for browser-driven E2E), not a
    /// framework hack — <c>IHostBuilder.Build()</c> may legitimately be called more than once,
    /// each call replaying the same accumulated configuration into an independent host/DI
    /// container.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (!_useRealKestrelServer) return base.CreateHost(builder);

        // Built but deliberately never Started: this app registers real IHostedServices (Quartz
        // schedulers, seed services, the outbox dispatcher) that mutate the SAME PostgreSQL
        // database the real Kestrel host below also starts — starting BOTH concurrently races
        // Quartz's own lock-table bootstrap against itself (observed directly: PostgreSQL 25P02,
        // "current transaction is aborted", from two StdRowLockSemaphore initializations running
        // at once). This throwaway host exists ONLY so WebApplicationFactory's own internal
        // bookkeeping (typed against TestServer) has a built host to hold onto; nothing ever
        // sends it a real request (Playwright never calls CreateClient()/Server on this factory),
        // so its hosted services never need to run at all.
        //
        // Development environment (set above) makes the generic host default ServiceProviderOptions
        // to ValidateOnBuild=true/ValidateScopes=true, which EAGERLY constructs every registered
        // singleton — including the local marketplace credential store, which opens an exclusive
        // (FileShare.None) lock file — during Build() itself, not merely on first use. Left on for
        // BOTH Build() calls below, the throwaway host's eager construction and the real host's own
        // eager construction race for the SAME exclusive lock file (same configuration, same path)
        // and the loser throws IOException (observed directly). Validation is real, useful
        // Development behavior that matters for the host actually serving traffic, so it is
        // disabled ONLY for this throwaway, never-requested host and left at its genuine default
        // for the real Kestrel host built right after.
        //
        // WebApplicationFactory's own DeferredHost machinery calls StartAsync() on WHATEVER this
        // method returns immediately after it returns — there is no way to prevent that call from
        // happening, only to make it harmless. Every IHostedService (Quartz schedulers, seed
        // services, the marketplace startup-recovery sweep) is therefore stripped from the
        // throwaway build's own services — confirmed necessary by direct observation: leaving them
        // in place raced this SAME throwaway host's hosted services against the real Kestrel
        // host's already-running ones for the exact same PostgreSQL rows and the exact same
        // credential-store lock file, regardless of which of the two this code itself called
        // Start() on.
        builder.ConfigureWebHost(webHostBuilder => webHostBuilder
            .UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = false;
                options.ValidateScopes = false;
            })
            .ConfigureServices(services => services.RemoveAll(typeof(IHostedService))));
        var throwawayTestHost = builder.Build();

        builder.ConfigureWebHost(webHostBuilder =>
        {
            webHostBuilder.UseKestrel();
            if (!string.IsNullOrWhiteSpace(_realKestrelUrls)) webHostBuilder.UseUrls(_realKestrelUrls);
            // Restores the genuine Development default for the host that actually serves traffic —
            // replayed last in the accumulated ConfigureWebHost sequence, so it wins over the
            // throwaway-only override above for THIS Build() call.
            webHostBuilder.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });
        });
        _realKestrelHost = builder.Build();
        _realKestrelHost.Start();

        var server = _realKestrelHost.Services.GetRequiredService<IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();
        RealServerAddresses = addressesFeature?.Addresses.ToArray() ?? [];

        return throwawayTestHost;
    }

    private static void RemoveEventLogProvider(IServiceCollection services)
    {
        var eventLogDescriptors = services
            .Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider)
                && descriptor.ImplementationType?.FullName == "Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider")
            .ToList();
        foreach (var descriptor in eventLogDescriptors) services.Remove(descriptor);
    }

    /// <summary>
    /// The cookie policy is SecurePolicy.Always (SECURITY §2.3) — the antiforgery AND auth
    /// cookies are both rejected/refused over a plain-HTTP request. TestServer's in-memory
    /// transport never does a real TLS handshake either way, so an https:// base address is
    /// the correct way to exercise the SAME cookie policy production actually runs under,
    /// not a workaround around it.
    /// </summary>
    public HttpClient CreateHttpsClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    public override async ValueTask DisposeAsync()
    {
        try
        {
            // Deliberately never lets a StopAsync failure (e.g. a host whose own StartAsync never
            // fully completed) skip Dispose() — Dispose() is what releases process-exclusive
            // resources a singleton may hold (e.g. the local credential store's own lock file), and
            // it must run even when graceful shutdown did not.
            if (_realKestrelHost is { } realHost)
            {
                try { await realHost.StopAsync(); }
                finally { realHost.Dispose(); }
            }
            await base.DisposeAsync();
        }
        finally
        {
            if (_environmentLockHeld)
            {
                // Leave the process exactly as this factory found it, THEN release the lock —
                // the next factory's constructor is guaranteed to see truly-original values even
                // if it doesn't override every key this instance did.
                RestoreAllEverSetKeysToOriginal();
                _environmentLockHeld = false;
                EnvironmentLock.Release();
            }
        }
    }
}
