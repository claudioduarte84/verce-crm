using System.Collections.Concurrent;

namespace Verce.Infrastructure.Marketplaces;

/// <summary>
/// Single-instance execution gate. The durable operation-row fence remains authoritative across
/// restart — this gate only prevents in-process interleaving between the callback's exclusive
/// exchange and ordinary shared credential-using operations for the same provider.
///
/// Deliberately NOT built on <see cref="ReaderWriterLockSlim"/>: that type is thread-affine (the
/// SAME OS thread that calls EnterWriteLock/EnterReadLock must call the matching Exit call), which
/// an async method violates the instant it resumes a continuation on a different thread-pool
/// thread after any <c>await</c> — exactly what <c>CompleteCallbackAsync</c> does while holding
/// this gate across multiple awaited database/store/provider calls. That combination throws
/// <see cref="SynchronizationLockException"/> ("write lock is being released without being held")
/// the first time a continuation lands on a different thread — reproduced by
/// <c>MarketplaceAuthorizationRt01Tests</c> the first time this workflow was actually exercised
/// end to end. <see cref="AsyncReaderWriterLock"/> below is a standard <see cref="SemaphoreSlim"/>-based
/// async reader/writer lock with no thread affinity.
/// </summary>
public sealed class ProviderExecutionGate
{
    private readonly ConcurrentDictionary<string, AsyncReaderWriterLock> _gates = new(StringComparer.OrdinalIgnoreCase);

    public Task<IAsyncDisposable> EnterExclusiveAsync(string providerCode, CancellationToken cancellationToken = default) =>
        _gates.GetOrAdd(providerCode, _ => new AsyncReaderWriterLock()).EnterWriteAsync(cancellationToken);

    public Task<IAsyncDisposable> EnterSharedAsync(string providerCode, CancellationToken cancellationToken = default) =>
        _gates.GetOrAdd(providerCode, _ => new AsyncReaderWriterLock()).EnterReadAsync(cancellationToken);
}

/// <summary>Async, non-thread-affine reader/writer lock: any number of concurrent readers, or
/// exactly one writer, never both — the classic "first reader takes the writer semaphore, last
/// reader releases it" construction.</summary>
internal sealed class AsyncReaderWriterLock
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _readerCountGate = new(1, 1);
    private int _readerCount;

    public async Task<IAsyncDisposable> EnterWriteAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        return new WriteReleaser(_writeGate);
    }

    public async Task<IAsyncDisposable> EnterReadAsync(CancellationToken cancellationToken)
    {
        await _readerCountGate.WaitAsync(cancellationToken);
        try
        {
            _readerCount++;
            if (_readerCount == 1) await _writeGate.WaitAsync(cancellationToken);
        }
        finally { _readerCountGate.Release(); }
        return new ReadReleaser(this);
    }

    private async Task ExitReadAsync()
    {
        await _readerCountGate.WaitAsync();
        try
        {
            _readerCount--;
            if (_readerCount == 0) _writeGate.Release();
        }
        finally { _readerCountGate.Release(); }
    }

    private sealed class WriteReleaser(SemaphoreSlim writeGate) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) writeGate.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReadReleaser(AsyncReaderWriterLock owner) : IAsyncDisposable
    {
        private int _released;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) await owner.ExitReadAsync();
        }
    }
}
