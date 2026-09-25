using System.Collections.Concurrent;
using System.Text;
using Verce.Modules.Commerce;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// ADR-0024 G-06: "Fake lives in test-support/harness only ... not in the production
/// Infrastructure assembly." This type lives in the integration test project only and is never
/// referenced by <c>Verce.Infrastructure.Marketplaces</c> or <c>Verce.Api</c>. It is wired into a
/// host only by a test-owned <c>ConfigureTestServices</c> override, never by production
/// composition (verified by <see cref="MarketplaceProductionIsolationTests"/>).
/// </summary>
public enum FakeMarketplaceOutcome { Success, DeniedBeforeSend, FailAfterSend }

public sealed record FakeMarketplaceScenario(
    string ExternalAccountId,
    IReadOnlyDictionary<string, AccountCapabilityState>? Grants = null,
    FakeMarketplaceOutcome Outcome = FakeMarketplaceOutcome.Success,
    CredentialImpact Impact = CredentialImpact.MAY_SUPERSEDE_EXISTING);

/// <summary>
/// Deterministic, in-memory, barrier-friendly control surface a test configures BEFORE it drives
/// a session through the real HTTP begin/callback endpoints. Keyed by session id (known to the
/// test from the begin response), never a static/global default — two tests running in the same
/// process never see each other's scenario.
/// </summary>
public sealed class FakeMarketplaceControlPlane
{
    private readonly ConcurrentDictionary<Guid, FakeMarketplaceScenario> _sessionScenarios = new();
    private readonly ConcurrentDictionary<string, string> _lastIssuedTokenByIdentity = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, (string ExternalAccountId, IReadOnlyDictionary<string, AccountCapabilityState> Grants)> _accountIdentities = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _barriers = new();
    private readonly ConcurrentDictionary<Guid, int> _completeOnceCallCounts = new();

    public void Configure(Guid sessionId, FakeMarketplaceScenario scenario) => _sessionScenarios[sessionId] = scenario;

    /// <summary>Callback security: "for duplicate/replayed callback, provider exchange call count
    /// must be &lt;= 1." Incremented once per genuine <see cref="FakeMarketplaceConnector.CompleteOnceAsync"/>
    /// invocation for a session — a replayed/duplicate HTTP callback that never reaches the
    /// connector (rejected earlier by the one-time claim) leaves this at 0 or 1, never higher.</summary>
    public int CompleteOnceCallCount(Guid sessionId) => _completeOnceCallCounts.GetValueOrDefault(sessionId);
    internal void RecordCompleteOnceCall(Guid sessionId) => _completeOnceCallCounts.AddOrUpdate(sessionId, 1, (_, count) => count + 1);

    public FakeMarketplaceScenario Resolve(Guid sessionId) =>
        _sessionScenarios.TryGetValue(sessionId, out var scenario) ? scenario : throw new InvalidOperationException("FAKE_MARKETPLACE_SCENARIO_NOT_CONFIGURED");

    /// <summary>RT-02: "issuing a new credential for identity X invalidates its previous fake
    /// credential for X" — a deterministic, test-only adversarial behavior, never a claim about a
    /// real provider. The previous token simply stops being the "current" one for that identity;
    /// nothing here enforces rejection on old-token use because S8C.1's own DB/operation arbiter
    /// (not the provider) is the authority that must make the old account non-executable.</summary>
    public string IssueToken(string externalAccountId)
    {
        var token = Guid.NewGuid().ToString("N");
        _lastIssuedTokenByIdentity[externalAccountId] = token;
        return token;
    }

    public void BindAccountIdentity(Guid accountId, string externalAccountId, IReadOnlyDictionary<string, AccountCapabilityState> grants) =>
        _accountIdentities[accountId] = (externalAccountId, grants);

    public (string ExternalAccountId, IReadOnlyDictionary<string, AccountCapabilityState> Grants)? IdentityFor(Guid accountId) =>
        _accountIdentities.TryGetValue(accountId, out var value) ? value : null;

    /// <summary>Lets a test hold CompleteOnce open on a TaskCompletionSource until it releases the
    /// barrier from another thread — used to race a resolver's row lock against a still-running
    /// exchange without ever using Thread.Sleep. A session with no <see cref="ArmBarrier"/> call
    /// resolves immediately — the barrier is opt-in per scenario, never a default stall.</summary>
    public Task WaitForReleaseAsync(Guid sessionId) => _barriers.GetOrAdd(sessionId, _ => AlreadyReleased()).Task;
    public void Release(Guid sessionId) => _barriers.GetOrAdd(sessionId, _ => AlreadyReleased()).TrySetResult();
    public void ArmBarrier(Guid sessionId) => _barriers[sessionId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource AlreadyReleased() { var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); tcs.SetResult(); return tcs; }
}

public sealed class FakeMarketplaceConnector : IMarketplaceAuthorizationConnector
{
    public const string Code = "FAKE";
    public string ProviderCode => Code;

    private readonly FakeMarketplaceControlPlane _control;
    public FakeMarketplaceConnector(FakeMarketplaceControlPlane control) => _control = control;

    // A real provider echoes the caller-supplied `state` back on its own redirect; the fake
    // simulates that by round-tripping it through the query string of the URI it hands back,
    // exactly like the real authorization URI would. Tests parse it back out — nothing server-side
    // ever returns the raw state value directly (ADR-0024 §2 route table).
    public Task<MarketplaceAuthorizationStart> BeginAsync(MarketplaceAuthorizationBeginRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new MarketplaceAuthorizationStart("https://fake-marketplace.test/authorize?session=" + request.SessionId + "&state=" + Uri.EscapeDataString(request.State)));

    public async Task<MarketplaceAuthorizationCompletion> CompleteOnceAsync(MarketplaceAuthorizationCompletionRequest request, CancellationToken cancellationToken)
    {
        _control.RecordCompleteOnceCall(request.SessionId);
        var scenario = _control.Resolve(request.SessionId);
        if (scenario.Outcome == FakeMarketplaceOutcome.DeniedBeforeSend)
            throw new MarketplaceRequestNotSentException("FAKE_MARKETPLACE_DENIED_BEFORE_SEND");

        // A barrier-armed session blocks here (simulating an in-flight exchange) until the test
        // releases it — used to race a startup/cleanup resolver against a "still exchanging" call.
        await _control.WaitForReleaseAsync(request.SessionId).WaitAsync(cancellationToken);

        if (scenario.Outcome == FakeMarketplaceOutcome.FailAfterSend)
            throw new InvalidOperationException("FAKE_MARKETPLACE_AMBIGUOUS_AFTER_SEND");

        var token = _control.IssueToken(scenario.ExternalAccountId);
        var grants = scenario.Grants ?? new Dictionary<string, AccountCapabilityState>();
        return new MarketplaceAuthorizationCompletion(scenario.ExternalAccountId, grants, Encoding.UTF8.GetBytes(token), scenario.Impact);
    }

    public Task<MarketplaceRefreshCompletion> RefreshAsync(MarketplaceRefreshRequest request, CancellationToken cancellationToken)
    {
        var identity = _control.IdentityFor(request.AccountId)?.ExternalAccountId ?? "FAKE-UNKNOWN";
        var token = _control.IssueToken(identity);
        return Task.FromResult(new MarketplaceRefreshCompletion(Encoding.UTF8.GetBytes(token), DateTimeOffset.UtcNow.AddHours(6)));
    }

    public Task<MarketplaceAccountInspection> InspectAsync(MarketplaceAccountInspectionRequest request, CancellationToken cancellationToken)
    {
        var identity = _control.IdentityFor(request.AccountId);
        return Task.FromResult(new MarketplaceAccountInspection(
            identity?.ExternalAccountId ?? "FAKE-UNKNOWN",
            identity?.Grants ?? new Dictionary<string, AccountCapabilityState>()));
    }
}
