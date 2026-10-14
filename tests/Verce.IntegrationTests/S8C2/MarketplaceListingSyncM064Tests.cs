using FluentAssertions;
using Verce.Infrastructure.Marketplaces;
using Verce.Modules.Commerce;

namespace Verce.IntegrationTests.S8C2;

/// <summary>M064: lifecycle guard for the process-wide static execution test hooks (see <see cref="ExecutionHooksCollection"/>).</summary>
public sealed partial class MarketplaceListingSyncExecutionIntegrationTests
{
    internal static List<string> ArmedExecutionHooks()
    {
        var armed = new List<string>();
        if (MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync is not null) armed.Add(nameof(MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync));
        if (MarketplaceListingSyncExecutionTestHooks.AfterAuthorizationValidationAsync is not null) armed.Add(nameof(MarketplaceListingSyncExecutionTestHooks.AfterAuthorizationValidationAsync));
        if (MarketplaceListingSyncExecutionTestHooks.AfterDetailSaveBeforeCommitAsync is not null) armed.Add(nameof(MarketplaceListingSyncExecutionTestHooks.AfterDetailSaveBeforeCommitAsync));
        if (MarketplaceListingSyncExecutionTestHooks.BeforeWriteRetryAsync is not null) armed.Add(nameof(MarketplaceListingSyncExecutionTestHooks.BeforeWriteRetryAsync));
        if (MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync is not null) armed.Add(nameof(MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync));
        if (MarketplaceListingSyncExecutionTestHooks.BeforeExhaustedTerminalizationAsync is not null) armed.Add(nameof(MarketplaceListingSyncExecutionTestHooks.BeforeExhaustedTerminalizationAsync));
        return armed;
    }

    /// <summary>The mechanism behind the M063 incident, proved deterministically: a static hook armed by ONE test is consumed by whichever execution in the process reaches its seam first —
    /// including an execution that belongs to a different test. Hence every class that executes runs shares one xUnit collection (<see cref="ExecutionHooksCollection"/>).</summary>
    [Fact]
    public async Task M064_a_static_execution_hook_is_consumed_by_the_first_execution_that_reaches_it_whoever_armed_it()
    {
        var account = await SeedAccountAsync(); var fired = 0;
        MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = _ => { fired++; return Task.CompletedTask; };      // "armed by another test"
        await using (var db = Context())
        {
            var run = await new MarketplaceListingSyncRunKernel(db).CreateFullAsync(account, Guid.NewGuid(), _clock.UtcNow);
            (await CreateExecution(db, new ScriptedReader(["MLB-HOOK-1"], _clock)).RunAsync(run.Id))!.Status.Should().Be(MarketplaceListingSyncStatus.SUCCEEDED);   // an unrelated execution
        }
        fired.Should().Be(1, "the unrelated execution consumed the hook");
        MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync.Should().BeNull("a hook is single-shot: the production seam clears it");
    }

    internal static void ClearExecutionHooks()
    {
        MarketplaceListingSyncExecutionTestHooks.BeforeFencedDetailPersistenceAsync = null;
        MarketplaceListingSyncExecutionTestHooks.AfterAuthorizationValidationAsync = null;
        MarketplaceListingSyncExecutionTestHooks.AfterDetailSaveBeforeCommitAsync = null;
        MarketplaceListingSyncExecutionTestHooks.BeforeWriteRetryAsync = null;
        MarketplaceListingSyncExecutionTestHooks.BeforeFinishAsync = null;
        MarketplaceListingSyncExecutionTestHooks.BeforeExhaustedTerminalizationAsync = null;
    }
}
