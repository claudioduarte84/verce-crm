using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Verce.Modules.Commerce;
using Verce.Platform.Persistence;

namespace Verce.Infrastructure.Marketplaces;

/// <summary>
/// The durable credential-operation guards shared by sync admission (start/retry/status) and by execution before every provider call, so the two
/// cannot drift apart (ADR-0024 §2/§5, ADR-0025 §G.2/§G.3). Both are queries over operation rows, never a second state authority. They are NOT the
/// process-local <see cref="ProviderExecutionGate"/>, which only serializes sends inside one process.
/// </summary>
public static class MarketplaceOperationFences
{
    /// <summary>A credential operation of this account is still PENDING (unresolved), so its credential state is not trustworthy yet.</summary>
    public static Task<bool> HasAccountPendingOperationAsync(VerceDbContext db, Guid accountId, CancellationToken ct) =>
        db.Set<MarketplaceAccountOperation>().AsNoTracking().AnyAsync(x => x.MarketplaceAccountId == accountId && x.Decision == MarketplaceAccountOperationDecision.PENDING, ct);

    /// <summary>The provider-wide callback safety fence: a CONNECT_NEW/RECONNECT operation of the provider that is PENDING in EXTERNAL_IN_FLIGHT or
    /// SECRET_PERSISTED, whatever its account (a CONNECT_NEW may not be bound to an account yet). PREPARED alone does not fence.</summary>
    public static Task<bool> HasProviderCallbackFenceAsync(VerceDbContext db, string providerCode, CancellationToken ct) =>
        db.Set<MarketplaceAccountOperation>().AsNoTracking().AnyAsync(x => x.ProviderCode == providerCode && x.Decision == MarketplaceAccountOperationDecision.PENDING
            && (x.Kind == MarketplaceAccountOperationKind.CONNECT_NEW || x.Kind == MarketplaceAccountOperationKind.RECONNECT)
            && (x.Phase == MarketplaceAccountOperationPhase.EXTERNAL_IN_FLIGHT || x.Phase == MarketplaceAccountOperationPhase.SECRET_PERSISTED), ct);

    public static async Task<bool> IsBlockedAsync(VerceDbContext db, Guid accountId, string providerCode, CancellationToken ct) =>
        await HasAccountPendingOperationAsync(db, accountId, ct) || await HasProviderCallbackFenceAsync(db, providerCode, ct);
}

/// <summary>Handoff B5 / ADR-0025 §E.1/§G.3: a process-wide cap on simultaneous provider requests per provider
/// (<see cref="MarketplaceListingSyncOptions.MaxInFlightRequestsPerProvider"/>). A VERCE default, not a provider quota; one semaphore per provider
/// code, no provider or credential material beyond the code, no distributed coordination.</summary>
public sealed class MarketplaceProviderInFlightLimiter(IOptions<MarketplaceListingSyncOptions> options)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private int _waiting;

    /// <summary>Callers currently waiting for a slot (observability seam for the deterministic concurrency tests).</summary>
    internal int Waiting => Volatile.Read(ref _waiting);

    public async Task<IDisposable> AcquireAsync(string providerCode, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(providerCode, _ => new SemaphoreSlim(options.Value.MaxInFlightRequestsPerProvider, options.Value.MaxInFlightRequestsPerProvider));
        Interlocked.Increment(ref _waiting);
        try { await gate.WaitAsync(cancellationToken); }
        finally { Interlocked.Decrement(ref _waiting); }
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
    }
}
