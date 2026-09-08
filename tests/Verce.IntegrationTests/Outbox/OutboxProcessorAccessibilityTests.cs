using System.Reflection;
using FluentAssertions;
using Verce.Platform.Outbox;

namespace Verce.IntegrationTests.Outbox;

/// <summary>
/// H-OUTBOX-002 / H-OUTBOX-003: <see cref="OutboxProcessor.RequeueAsync"/> and
/// <see cref="OutboxProcessor.DismissAsync"/> are manual administrative mutation primitives with
/// no actor authorization or audit boundary of their own — <see cref="OutboxAdministrationService"/>
/// is the only supported entry point for a requeue, and S1 ships no dismiss entry point at all.
/// Both must stay non-public so no future caller outside this assembly can bypass that boundary
/// by calling the primitive directly. Pure reflection over type metadata — no database, no host,
/// no environment mutation — so this needs neither a Postgres fixture nor collection membership.
/// </summary>
public class OutboxProcessorAccessibilityTests
{
    private const BindingFlags AnyInstanceMember = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void RequeueAsync_is_not_publicly_callable()
    {
        var method = typeof(OutboxProcessor).GetMethod(nameof(OutboxProcessor.RequeueAsync), AnyInstanceMember);

        method.Should().NotBeNull("the fenced requeue primitive must still exist for OutboxAdministrationService and its own tests to call");
        method!.IsPublic.Should().BeFalse("a public RequeueAsync is exactly the H-OUTBOX-002 bypass — only OutboxAdministrationService may requeue from outside this assembly");
        method.IsAssembly.Should().BeTrue("internal (assembly-visible, via InternalsVisibleTo for this test project), not private — OutboxAdministrationService lives in the same assembly and calls it directly");
    }

    [Fact]
    public void DismissAsync_is_not_publicly_callable()
    {
        var method = typeof(OutboxProcessor).GetMethod(nameof(OutboxProcessor.DismissAsync), AnyInstanceMember);

        method.Should().NotBeNull("the fenced dismissal primitive must still exist for its own low-level tests to call");
        method!.IsPublic.Should().BeFalse("a public DismissAsync is exactly the H-OUTBOX-003 bypass — S1 has no supported dismiss entry point at all, so nothing outside this assembly may call it");
        method.IsAssembly.Should().BeTrue("internal (assembly-visible, via InternalsVisibleTo for this test project), not private");
    }

    // ---- Public surface scan (mission §11/§21): legitimate worker/runtime primitives must stay
    // public — this is deliberate documentation-as-a-test, not a call to internalize them.

    [Theory]
    [InlineData(nameof(OutboxProcessor.ClaimBatchAsync))]
    [InlineData(nameof(OutboxProcessor.CompleteAsync))]
    [InlineData(nameof(OutboxProcessor.FailRetryableAsync))]
    [InlineData(nameof(OutboxProcessor.FailNonRetryableAsync))]
    [InlineData(nameof(OutboxProcessor.HeartbeatAsync))]
    [InlineData(nameof(OutboxProcessor.ReclaimExpiredLeasesAsync))]
    public void Worker_runtime_primitives_remain_public(string methodName)
    {
        var method = typeof(OutboxProcessor).GetMethod(methodName, AnyInstanceMember);

        method.Should().NotBeNull();
        method!.IsPublic.Should().BeTrue(
            $"{methodName} is a legitimate platform runtime API (used by OutboxDispatcher/OutboxJobs), " +
            "not an unauthenticated administrative mutation bypass — it must not be internalized as a side effect of H-OUTBOX-002/003");
    }
}
