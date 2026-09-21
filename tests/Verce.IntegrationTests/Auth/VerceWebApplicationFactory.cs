using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    private bool _environmentLockHeld;

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
    public VerceWebApplicationFactory(string connectionString, IReadOnlyDictionary<string, string?>? extraConfiguration = null, IClock? clockOverride = null)
    {
        _connectionString = connectionString;
        _extraConfiguration = extraConfiguration ?? new Dictionary<string, string?>();
        _clockOverride = clockOverride;

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
        });
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
