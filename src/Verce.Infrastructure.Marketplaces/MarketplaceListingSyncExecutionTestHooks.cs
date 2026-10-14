namespace Verce.Infrastructure.Marketplaces;

/// <summary>Internal, normally-null deterministic seams for the PostgreSQL transaction proofs.
/// They are assembly-visible only to the integration test project and are never registered in DI.</summary>
internal static class MarketplaceListingSyncExecutionTestHooks
{
    internal static Func<CancellationToken, Task>? BeforeFencedDetailPersistenceAsync;
    internal static Func<CancellationToken, Task>? AfterAuthorizationValidationAsync;
    internal static Func<CancellationToken, Task>? AfterDetailSaveBeforeCommitAsync;
    internal static Func<int, CancellationToken, Task>? BeforeWriteRetryAsync;
    /// <summary>Runs after the last PENDING item is resolved and before the finish transaction starts (outside any transaction).</summary>
    internal static Func<CancellationToken, Task>? BeforeFinishAsync;
    /// <summary>Runs in the dispatcher after an exhausted run was read and before its terminalization compare-and-swap (outside any transaction).</summary>
    internal static Func<Guid, CancellationToken, Task>? BeforeExhaustedTerminalizationAsync;
}

internal sealed class MarketplaceListingSyncTestRollbackException : Exception { }
