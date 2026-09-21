using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Audit;
using Verce.Modules.Production;
using Verce.Modules.Quoting.Contracts;
using Verce.Platform.Numbering;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;

namespace Verce.IntegrationTests.Production;

/// <summary>
/// B-04/F-02: <see cref="CreateProductionOrderOnQuoteApproved"/> must converge idempotently under
/// both sequential replay (the same event dispatched twice) and REAL concurrent execution (two
/// independent transactions racing for the same <c>quote_revision_id</c>) — never a TOCTOU
/// check-then-insert race, never an unhandled PostgreSQL unique-violation, and — since F-02 —
/// never consuming a <see cref="SequentialNumberAllocator"/> number for a replay or a losing
/// racer. <c>quote_id</c>/<c>quote_revision_id</c> are plain ID references with no FK (CLAUDE.md
/// rule 11), so this exercises the handler directly against arbitrary GUIDs without needing a
/// real Quote.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProductionOrderIdempotencyTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public ProductionOrderIdempotencyTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE production.production_order, quoting.quote_number_counter, platform.audit_log RESTART IDENTITY CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static QuoteApprovedEvent NewApprovalEvent(Guid quoteId, Guid quoteRevisionId) =>
        new(Guid.NewGuid(), null, DateTimeOffset.UtcNow, quoteId, quoteRevisionId, DateTimeOffset.UtcNow, Guid.NewGuid());

    private static AmbientOperationContext NewAmbientContext() =>
        new(Guid.NewGuid(), AuditSource.Api, DateTimeOffset.UtcNow);

    private async Task<int> CurrentCounterSequenceAsync()
    {
        await using var db = _fixture.CreateContext();
        // .ToListAsync() sends the raw SQL as-is; composing .SingleOrDefaultAsync() directly onto
        // a SqlQueryRaw<int> queryable instead makes EF wrap it in a subquery expecting a column
        // literally named "Value", which this raw SELECT does not have.
        var rows = await db.Database.SqlQueryRaw<int>(
            "SELECT last_sequence FROM quoting.quote_number_counter WHERE series = 'PRODUCTION_ORDER'")
            .ToListAsync();
        return rows.SingleOrDefault();
    }

    private async Task<int> CreationAuditCountAsync(Guid entityId)
    {
        await using var db = _fixture.CreateContext();
        return await db.Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.EntityTable == "production_order" && a.EntityId == entityId && a.Operation == "ADDED")
            .CountAsync();
    }

    private async Task<ProductionOrder> LoadOrderByRevisionAsync(Guid quoteRevisionId)
    {
        await using var db = _fixture.CreateContext();
        return await db.Set<ProductionOrder>().AsNoTracking().SingleAsync(o => o.QuoteRevisionId == quoteRevisionId);
    }

    private async Task<ProductionOrder> LoadOrderByIdAsync(Guid id)
    {
        await using var db = _fixture.CreateContext();
        return await db.Set<ProductionOrder>().AsNoTracking().SingleAsync(o => o.Id == id);
    }

    [Fact]
    public async Task Replaying_the_same_QuoteApproved_event_twice_creates_exactly_one_order_never_advances_the_counter_and_the_next_real_order_gets_the_very_next_sequence()
    {
        var quoteId = Guid.NewGuid();
        var quoteRevisionId = Guid.NewGuid();

        await using var db = _fixture.CreateContext();
        var handler = new CreateProductionOrderOnQuoteApproved(NewAmbientContext());

        await handler.HandleAsync(NewApprovalEvent(quoteId, quoteRevisionId), db, CancellationToken.None);
        await db.SaveChangesAsync();

        var afterFirst = await LoadOrderByRevisionAsync(quoteRevisionId);
        afterFirst.NumberSequence.Should().Be(1);
        var counterAfterFirst = await CurrentCounterSequenceAsync();
        counterAfterFirst.Should().Be(1);
        (await CreationAuditCountAsync(afterFirst.Id)).Should().Be(1, "exactly one Created audit for the genuine creator");

        // F-02: second execution reaches the SAME Production logic (not a short-circuit at a
        // higher layer) and must still complete cleanly, without a second row, without throwing,
        // and — critically — WITHOUT consuming another PRODUCTION_ORDER sequence number.
        var act = async () =>
        {
            await handler.HandleAsync(NewApprovalEvent(quoteId, quoteRevisionId), db, CancellationToken.None);
            await db.SaveChangesAsync();
        };
        await act.Should().NotThrowAsync();

        await using var verify = _fixture.CreateContext();
        var orders = await verify.Set<ProductionOrder>().Where(o => o.QuoteRevisionId == quoteRevisionId).ToListAsync();
        orders.Should().ContainSingle();
        orders[0].Status.Should().Be(ProductionOrderStatus.QUEUED);
        orders[0].NumberSequence.Should().Be(1, "the replay must not renumber the existing order");
        var counterAfterReplay = await CurrentCounterSequenceAsync();
        counterAfterReplay.Should().Be(1, "a replay must never advance the daily sequence counter");
        (await CreationAuditCountAsync(orders[0].Id)).Should().Be(1, "the replay must never write a second Created audit");

        // A genuinely NEW order (different revision) must get the very next sequence — proving
        // the replay above left no gap for it to inherit.
        var secondQuoteId = Guid.NewGuid();
        var secondRevisionId = Guid.NewGuid();
        await handler.HandleAsync(NewApprovalEvent(secondQuoteId, secondRevisionId), db, CancellationToken.None);
        await db.SaveChangesAsync();
        var secondOrder = await LoadOrderByRevisionAsync(secondRevisionId);
        secondOrder.NumberSequence.Should().Be(2, "previous sequence + 1, never +2 — the replay consumed nothing");
    }

    [Fact]
    public async Task Two_independent_transactions_racing_for_the_same_quote_revision_converge_to_exactly_one_row_consume_exactly_one_number_and_write_exactly_one_creation_audit()
    {
        var quoteId = Guid.NewGuid();
        var quoteRevisionId = Guid.NewGuid();
        var evt = NewApprovalEvent(quoteId, quoteRevisionId);

        await using var db1 = _fixture.CreateContext();
        await using var db2 = _fixture.CreateContext();
        await using var tx1 = await db1.Database.BeginTransactionAsync();
        await using var tx2 = await db2.Database.BeginTransactionAsync();

        var handler1 = new CreateProductionOrderOnQuoteApproved(NewAmbientContext());
        var handler2 = new CreateProductionOrderOnQuoteApproved(NewAmbientContext());

        // Genuinely concurrent: both handlers reach pg_advisory_xact_lock before either commits.
        // F-02: the SECOND caller must block on that exact lock (never on the ON CONFLICT insert,
        // which by design only the serialized winner ever reaches) until the first transaction's
        // COMMIT/ROLLBACK releases it — proving the concurrency boundary is the advisory lock,
        // not merely "whoever wins the number counter row".
        var task1 = RunAndCommitAsync(handler1, evt, db1, tx1);
        var task2 = RunAndCommitAsync(handler2, evt, db2, tx2);

        var act = async () => await Task.WhenAll(task1, task2);
        await act.Should().NotThrowAsync("neither transaction may ever surface a PostgreSQL unique-violation or a 25P02 abort to its caller");

        await using var verify = _fixture.CreateContext();
        var orders = await verify.Set<ProductionOrder>().Where(o => o.QuoteRevisionId == quoteRevisionId).ToListAsync();
        orders.Should().ContainSingle("exactly one ProductionOrder must survive the race, regardless of which transaction won");
        orders[0].NumberSequence.Should().Be(1, "exactly one PRODUCTION_ORDER sequence number is consumed by the race, never two");
        (await CreationAuditCountAsync(orders[0].Id)).Should().Be(1, "exactly one Created audit survives — the loser never writes one");

        var counterAfterRace = await CurrentCounterSequenceAsync();
        counterAfterRace.Should().Be(1, "the counter proves the loser never advanced it — only the single winner did");

        // A subsequent, unrelated approval must get sequence 2, not 3 — proving the loser's
        // advisory-lock wait, not a wasted number allocation, was what serialized it.
        var thirdRevisionId = Guid.NewGuid();
        var handler3 = new CreateProductionOrderOnQuoteApproved(NewAmbientContext());
        await using var db3 = _fixture.CreateContext();
        await handler3.HandleAsync(NewApprovalEvent(Guid.NewGuid(), thirdRevisionId), db3, CancellationToken.None);
        await db3.SaveChangesAsync();
        var thirdOrder = await LoadOrderByRevisionAsync(thirdRevisionId);
        thirdOrder.NumberSequence.Should().Be(2);
    }

    [Fact]
    public async Task Replaying_a_supersession_approval_never_double_cancels_the_previous_order_or_reassigns_its_supersession_pointer()
    {
        var quoteId = Guid.NewGuid();
        var r1RevisionId = Guid.NewGuid();
        var r2RevisionId = Guid.NewGuid();

        await using var db = _fixture.CreateContext();
        var handler = new CreateProductionOrderOnQuoteApproved(NewAmbientContext());

        // R1 approved -> order A (QUEUED).
        await handler.HandleAsync(NewApprovalEvent(quoteId, r1RevisionId), db, CancellationToken.None);
        await db.SaveChangesAsync();
        var orderA = await LoadOrderByRevisionAsync(r1RevisionId);

        // R2 approved (same Quote) -> order B (QUEUED), A superseded/CANCELED.
        var r2Event = NewApprovalEvent(quoteId, r2RevisionId);
        await handler.HandleAsync(r2Event, db, CancellationToken.None);
        await db.SaveChangesAsync();
        var orderBId = (await LoadOrderByRevisionAsync(r2RevisionId)).Id;
        var orderAAfterFirst = await LoadOrderByIdAsync(orderA.Id);
        orderAAfterFirst.Status.Should().Be(ProductionOrderStatus.CANCELED);
        orderAAfterFirst.CancellationReason.Should().Be("SUPERSEDED_BY_REVISION");
        orderAAfterFirst.SupersededByOrderId.Should().Be(orderBId);

        // Replay the EXACT SAME R2-approved event — B already exists (no renumbering), and A is
        // already CANCELED so the matrix's own status-gated switch leaves it untouched (never
        // re-invokes CancelAsSupersededByRevision, which would throw PRODUCTION_ORDER_NOT_QUEUED
        // on an already-terminal order).
        var act = async () =>
        {
            await handler.HandleAsync(r2Event, db, CancellationToken.None);
            await db.SaveChangesAsync();
        };
        await act.Should().NotThrowAsync();

        await using var verify = _fixture.CreateContext();
        var orderAAfterReplay = await verify.Set<ProductionOrder>().AsNoTracking().SingleAsync(o => o.Id == orderA.Id);
        orderAAfterReplay.Status.Should().Be(ProductionOrderStatus.CANCELED, "never double-canceled");
        orderAAfterReplay.SupersededByOrderId.Should().Be(orderBId, "never reassigned to a different order after being set");
        (await verify.Set<ProductionOrder>().CountAsync(o => o.QuoteId == quoteId)).Should().Be(2, "no third order is ever created by the replay");
        var counterAfterReplay = await CurrentCounterSequenceAsync();
        counterAfterReplay.Should().Be(2, "only A and B ever consumed a number — the replay consumed none");
    }

    private static async Task RunAndCommitAsync(CreateProductionOrderOnQuoteApproved handler, QuoteApprovedEvent evt,
        VerceDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx)
    {
        await handler.HandleAsync(evt, db, CancellationToken.None);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}
