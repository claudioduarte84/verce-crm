using System.Linq;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Verce.Infrastructure.Marketplaces.MercadoLivre;
using Verce.Modules.Commerce;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// A-10: "stop using FakeMarketplaceConnector.RefreshAsync invocation count as proof of an HTTP
/// send. Instrument at the actual HttpMessageHandler/SendAsync boundary for the real ML adapter."
/// This handler IS that instrumentation point — every test in this harness family drives the REAL
/// <see cref="MercadoLivreConnector"/> (never the S8C.1 fake), through the REAL
/// <c>MarketplaceAuthorizationWorkflow</c>/<c>MarketplaceCredentialExecutor</c>, against real
/// Testcontainers PostgreSQL and a real <c>LocalProtectedCredentialStore</c> — only the two named
/// HTTP clients' transport is swapped for this in-process, no-sockets handler.
/// </summary>
public sealed class RealAdapterHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly Queue<Func<HttpRequestMessage, Exception>> _faults = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly object _gate = new();

    /// <summary>The one number A-10 insists on: how many times SendAsync was ACTUALLY invoked —
    /// never inferred from a fake connector's own bookkeeping.</summary>
    public int SendAsyncInvocationCount { get; private set; }
    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    /// <summary>Zeroes the invocation counter — tests call this after using the harness's own
    /// connect-account setup calls, so each test's OWN assertion measures only the calls its own
    /// scenario made, never the setup's.</summary>
    public void ResetInvocationCount() { lock (_gate) { SendAsyncInvocationCount = 0; _requests.Clear(); } }

    public void EnqueueResponse(Func<HttpRequestMessage, HttpResponseMessage> respond) { lock (_gate) _responses.Enqueue(respond); }
    public void EnqueueResponse(HttpResponseMessage response) => EnqueueResponse(_ => response);
    public void EnqueueFault(Func<HttpRequestMessage, Exception> fault) { lock (_gate) _faults.Enqueue(fault); }
    public void EnqueueFault(Exception exception) => EnqueueFault(_ => exception);

    // A-01: a genuine post-send cancellation seam. Arming the barrier makes the NEXT SendAsync
    // call (only that one — one-shot) do three REAL things, in order: (1) count itself and record
    // the request EXACTLY like any other call — so "SendAsync was invoked" is never in doubt; (2)
    // signal the TaskCompletionSource `WaitForSendEnteredAsync` returns, so a test can learn FOR
    // CERTAIN that SendAsync has started, without polling or an arbitrary delay; (3) block on a
    // second TaskCompletionSource, observed via `.WaitAsync(cancellationToken)` — a REAL await on
    // the CALLER's own token, so a genuine `CancellationTokenSource.Cancel()` from the test throws
    // a real `OperationCanceledException` out of this handler exactly as a slow real socket read
    // would. No response/fault is dequeued until release (or cancellation) resolves this wait —
    // a test never armed this barrier sees IDENTICAL behavior to before (immediate dequeue).
    private TaskCompletionSource? _armedEnteredSignal;
    private TaskCompletionSource? _armedReleaseGate;

    /// <summary>Arms the one-shot barrier and returns the Task that completes the moment the NEXT
    /// SendAsync call has been counted/recorded — i.e. proven, not inferred, that the send happened.</summary>
    public Task WaitForNextSendEnteredAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) { _armedEnteredSignal = entered; _armedReleaseGate = release; }
        return entered.Task;
    }

    /// <summary>Releases the barrier armed by <see cref="WaitForNextSendEnteredAsync"/> — the
    /// blocked SendAsync call resumes and dequeues its scripted response/fault normally. A no-op if
    /// nothing is currently armed/blocked.</summary>
    public void ReleaseSendBarrier()
    {
        TaskCompletionSource? release;
        lock (_gate) release = _armedReleaseGate;
        release?.TrySetResult();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        TaskCompletionSource? entered;
        TaskCompletionSource? release;
        lock (_gate)
        {
            SendAsyncInvocationCount++;
            _requests.Add(request);
            entered = _armedEnteredSignal;
            release = _armedReleaseGate;
            _armedEnteredSignal = null;
            _armedReleaseGate = null; // one-shot: only the call the test actually armed for blocks
        }
        entered?.TrySetResult();
        if (release is not null) await release.Task.WaitAsync(cancellationToken); // genuine cancellation observed HERE, mid-flight

        lock (_gate)
        {
            if (_faults.Count > 0) throw _faults.Dequeue()(request);
            if (_responses.Count > 0) return _responses.Dequeue()(request);
            throw new InvalidOperationException("RealAdapterHttpHandler: no scripted response/fault queued for this call.");
        }
    }

    /// <summary>A structurally-truncated body: declares a Content-Length larger than what the
    /// stream actually yields, so .NET's own HttpContent reader observes end-of-stream before the
    /// promised byte count and throws — a real "headers received, body truncated" shape, not an
    /// inferred/simulated exception type.</summary>
    public static HttpResponseMessage TruncatedBodyResponse(int declaredLength, byte[] actualBytes) =>
        new(HttpStatusCode.OK) { Content = new TruncatedContent(actualBytes, declaredLength) };

    private sealed class TruncatedContent(byte[] bytes, int declaredLength) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes);
            // Deliberately never writes the remaining declared-but-missing bytes, then throws —
            // exactly what a connection that dies mid-body looks like to the reader.
            throw new IOException("Simulated truncated response body (test fixture, never a real network failure).");
        }
        protected override bool TryComputeLength(out long length) { length = declaredLength; return true; }
    }
}

/// <summary>Wires the REAL <see cref="MercadoLivreConnector"/> into a <see cref="Auth.VerceWebApplicationFactory"/>
/// with a disposable credential-store root (same discipline as <see cref="MarketplaceTestHarness"/>)
/// and both named HTTP clients' transport replaced by <see cref="RealAdapterHttpHandler"/> — never
/// real sockets, never the internet, never a real seller/app credential. Optionally decorates the
/// real <see cref="IProtectedCredentialStore"/> with <see cref="FaultInjectingCredentialStore"/>
/// (A-10 rows 18/19) when <see cref="InjectCasFaults"/> is set before the factory is constructed.</summary>
public sealed class MercadoLivreRealAdapterHarness : IDisposable
{
    public string CredentialStoreRoot { get; }
    public RealAdapterHttpHandler Handler { get; } = new();
    public IReadOnlyDictionary<string, string?> ExtraConfiguration { get; }
    public bool InjectCasFaults { get; init; }
    public FaultInjectingCredentialStore? FaultStore { get; private set; }

    public MercadoLivreRealAdapterHarness()
    {
        CredentialStoreRoot = Path.Combine(Path.GetTempPath(), "verce-ml-real-adapter-test-" + Guid.NewGuid().ToString("N"));
        ExtraConfiguration = new Dictionary<string, string?>
        {
            ["Marketplaces:CredentialStoreRoot"] = CredentialStoreRoot,
            ["Marketplaces:Cleanup:SchedulingEnabled"] = "false",
            [$"{MercadoLivreOptions.SectionName}:Enabled"] = "true",
            [$"{MercadoLivreOptions.SectionName}:ClientId"] = "900000001",
            [$"{MercadoLivreOptions.SectionName}:ClientSecret"] = "test-only-synthetic-secret-never-real",
            [$"{MercadoLivreOptions.SectionName}:RedirectUri"] = "https://verce-ml-test.local/api/commerce/marketplace-authorizations/MERCADO_LIVRE/callback",
            // A-09: the ONE authoritative public origin (Verce:PublicOrigin), not a second
            // ML-specific "expected origin" — this harness's RedirectUri above must agree with it.
            [$"{Verce.Platform.PublicOriginOptions.SectionName}:PublicOrigin"] = "https://verce-ml-test.local",
            [$"{MercadoLivreOptions.SectionName}:AccessTokenRefreshMarginSeconds"] = "300",
        };
    }

    public void ConfigureServices(IServiceCollection services)
    {
        // Registered AFTER Program.cs's own AddVerceMercadoLivre — Microsoft.Extensions.Http applies
        // ConfigurePrimaryHttpMessageHandler delegates in registration order, so this later one wins
        // and becomes the effective transport for both named clients; production code is untouched.
        services.AddHttpClient("MercadoLivre.OAuth").ConfigurePrimaryHttpMessageHandler(() => Handler);
        services.AddHttpClient("MercadoLivre.Api").ConfigurePrimaryHttpMessageHandler(() => Handler);

        if (InjectCasFaults)
        {
            // Decorates whatever REAL IProtectedCredentialStore Program.cs already registered
            // (LocalProtectedCredentialStore) — never a mock/fake store — so the workflow's own
            // CAS-retry logic is proven against genuine encrypted-file persistence. Re-registering
            // the concrete type separately (rather than resolving IProtectedCredentialStore itself
            // inside its own replacement factory) avoids a self-referential resolution cycle.
            var existingDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IProtectedCredentialStore));
            if (existingDescriptor?.ImplementationType is { } concreteType)
            {
                services.Remove(existingDescriptor);
                services.Add(new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(concreteType, concreteType, ServiceLifetime.Singleton));
                services.AddSingleton<IProtectedCredentialStore>(sp =>
                {
                    var inner = (IProtectedCredentialStore)sp.GetRequiredService(concreteType);
                    FaultStore = new FaultInjectingCredentialStore(inner);
                    return FaultStore;
                });
            }
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(CredentialStoreRoot)) Directory.Delete(CredentialStoreRoot, recursive: true); }
        catch { /* best-effort; OS temp directory is reclaimed anyway */ }
    }
}

/// <summary>
/// A-10 (rows 18/19): a CAS-fault-injecting decorator over a REAL <see cref="IProtectedCredentialStore"/>
/// (never a fake store) — the only way to prove the workflow's own "exactly one additional
/// persistence attempt of the identical R2" logic against a real encrypted-file store, not a mock.
/// </summary>
public sealed class FaultInjectingCredentialStore : IProtectedCredentialStore
{
    private readonly IProtectedCredentialStore _inner;
    private readonly Queue<ProtectedCredentialStoreOutcome?> _casOutcomeOverrides = new();
    private readonly Queue<Exception?> _casFaults = new();
    private readonly Queue<Exception?> _useConfirmedFaults = new();
    public List<ReadOnlyMemory<byte>> ObservedCasSecretMaterial { get; } = [];

    /// <summary>A-02: a SHA-256 fingerprint of the EXACT bytes <see cref="CompareAndSwapReplaceAsync"/>
    /// was called with, computed synchronously BEFORE delegating to the real store (or short-
    /// circuiting to an injected outcome/fault) — i.e. at the one moment those bytes are
    /// guaranteed to still be the genuine plaintext R2, never a value read back after the
    /// workflow's own `finally` block may already have zeroed the underlying array. A test compares
    /// this against a known-good hash of the expected plaintext to prove the CAS call genuinely
    /// received real secret material, then separately inspects the SAME backing array (recoverable
    /// via <see cref="System.Runtime.InteropServices.MemoryMarshal.TryGetArray{T}"/> on
    /// <see cref="ObservedCasSecretMaterial"/>) after the workflow call returns, to prove THAT
    /// array — not a copy of it — is now all-zero.</summary>
    public List<string> ObservedCasSecretFingerprints { get; } = [];
    public int CasAttemptCount { get; private set; }
    public int UseConfirmedCallCount { get; private set; }

    public FaultInjectingCredentialStore(IProtectedCredentialStore inner) => _inner = inner;

    /// <summary>Queues one override per upcoming <see cref="CompareAndSwapReplaceAsync"/> call —
    /// null means "let the real store decide"; a non-null outcome short-circuits that ONE call
    /// without ever touching the real store, so the decorator can prove the exact retry count.</summary>
    public void EnqueueCasOutcome(ProtectedCredentialStoreOutcome? outcome) => _casOutcomeOverrides.Enqueue(outcome);

    /// <summary>A-02 test support: queues an exception (e.g. <see cref="OperationCanceledException"/>)
    /// to throw on the NEXT <see cref="CompareAndSwapReplaceAsync"/> call — AFTER recording its
    /// fingerprint/reference, exactly like a genuine cancellation observed right as the provider's
    /// response is being persisted would — proving zeroization still happens on this exit path too.</summary>
    public void EnqueueCasFault(Exception fault) => _casFaults.Enqueue(fault);

    /// <summary>A-01 test support: queues an exception (e.g. <see cref="OperationCanceledException"/>)
    /// to throw on the NEXT <see cref="UseConfirmedAsync"/> call instead of delegating to the real
    /// store — a deterministic way to prove "cancellation/failure during the pre-SendAsync
    /// credential read" without a real race.</summary>
    public void EnqueueUseConfirmedFault(Exception fault) => _useConfirmedFaults.Enqueue(fault);

    // A-01/A-10 (Sol re-gate): a GENUINE real-cancellation seam for UseConfirmedAsync — never a
    // pre-built exception. Arming this makes the NEXT UseConfirmedAsync call (one-shot) signal
    // "entered" (proving the credential read has genuinely begun) and then observe the CALLER'S OWN
    // cancellationToken via `Task.Delay(Timeout.Infinite, cancellationToken)`: this never completes
    // on its own — it throws a REAL OperationCanceledException the instant (and only when) that
    // exact token is canceled, exactly like a genuine slow decrypt/file-read would. A test that
    // arms this MUST eventually cancel the token it gave the caller, or the awaited delay never
    // resolves.
    private TaskCompletionSource? _armedUseConfirmedEnteredSignal;
    private bool _armedUseConfirmedBarrier;

    /// <summary>Arms the one-shot barrier and returns the Task that completes the moment the NEXT
    /// UseConfirmedAsync call has been counted — proof, not inference, that the credential read has
    /// genuinely begun before the test cancels anything.</summary>
    public Task WaitForNextUseConfirmedEnteredAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _armedUseConfirmedEnteredSignal = entered;
        _armedUseConfirmedBarrier = true;
        return entered.Task;
    }

    public Task<ProtectedCredentialStoreResult> CreateStagedAsync(ProtectedCredentialWrite write, CancellationToken cancellationToken) => _inner.CreateStagedAsync(write, cancellationToken);
    public Task<ProtectedCredentialStoreResult> BindCandidateAsync(ProtectedCredentialBinding binding, CancellationToken cancellationToken) => _inner.BindCandidateAsync(binding, cancellationToken);
    public Task<ProtectedCredentialStoreResult> PromoteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken cancellationToken) => _inner.PromoteCandidateAsync(operationId, credentialReference, credentialVersion, cancellationToken);
    public async Task<ProtectedCredentialReadResult> UseConfirmedAsync(ProtectedCredentialRead read, CancellationToken cancellationToken)
    {
        UseConfirmedCallCount++;
        if (_useConfirmedFaults.Count > 0 && _useConfirmedFaults.Dequeue() is { } fault) throw fault;
        if (_armedUseConfirmedBarrier)
        {
            _armedUseConfirmedBarrier = false;
            _armedUseConfirmedEnteredSignal?.TrySetResult();
            // Never completes on its own — only a REAL cancellation of THIS EXACT token (the
            // caller's own, propagated all the way down from MarketplaceAuthorizationWorkflow.
            // RefreshAsync's own parameter) throws. No pre-built exception anywhere in this path.
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable: Task.Delay(Timeout.Infinite, ...) only ever returns via a thrown OperationCanceledException");
        }
        return await _inner.UseConfirmedAsync(read, cancellationToken);
    }
    public Task<ProtectedCredentialStoreResult> DeleteCandidateAsync(Guid operationId, string credentialReference, long credentialVersion, CancellationToken cancellationToken) => _inner.DeleteCandidateAsync(operationId, credentialReference, credentialVersion, cancellationToken);

    public async Task<ProtectedCredentialStoreResult> CompareAndSwapReplaceAsync(ProtectedCredentialWrite write, long expectedVersion, CancellationToken cancellationToken)
    {
        CasAttemptCount++;
        ObservedCasSecretMaterial.Add(write.SecretMaterial);
        // Computed NOW, synchronously, before any fault/outcome short-circuit and before the real
        // store call — the one moment these bytes are guaranteed to still be genuine plaintext.
        ObservedCasSecretFingerprints.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(write.SecretMaterial.Span)));
        if (_casFaults.Count > 0 && _casFaults.Dequeue() is { } fault) throw fault;
        if (_casOutcomeOverrides.Count > 0)
        {
            var overrideOutcome = _casOutcomeOverrides.Dequeue();
            if (overrideOutcome is { } outcome) return new ProtectedCredentialStoreResult(outcome);
        }
        return await _inner.CompareAndSwapReplaceAsync(write, expectedVersion, cancellationToken);
    }
}
