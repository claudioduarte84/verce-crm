using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Verce.Platform.Numbering;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.Quoting;

/// <summary>
/// H-01: <see cref="SequentialNumberAllocator"/> is the shared, per-business-date atomic sequence
/// behind ADR-0004 §1 — this proves the invariant against a REAL PostgreSQL instance rather than
/// only inline as an incidental assertion inside an unrelated HTTP test: concurrent allocations on
/// the same organization-local day all succeed with unique, contiguous sequences and no
/// duplicate-key failure, and a rolled-back transaction's allocation rolls back with it (no gap
/// left behind for the NEXT real allocation to inherit).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QuoteNumberConcurrencyTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public QuoteNumberConcurrencyTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE quoting.quote_number_counter RESTART IDENTITY CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Concurrent_allocations_on_the_same_organization_day_are_all_unique_with_no_duplicate_key_failure()
    {
        var today = new DateOnly(2026, 9, 20);
        const int concurrency = 20;

        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            await using var db = _fixture.CreateContext();
            await using var tx = await db.Database.BeginTransactionAsync();
            var sequence = await SequentialNumberAllocator.AllocateAsync(db, SequentialNumberAllocator.QuoteSeries, today, CancellationToken.None);
            await tx.CommitAsync();
            return sequence;
        }).ToArray();

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync("no concurrent allocation may ever surface a PostgreSQL unique-violation");

        var sequences = await Task.WhenAll(tasks);
        sequences.Should().OnlyHaveUniqueItems();
        sequences.OrderBy(s => s).Should().BeEquivalentTo(Enumerable.Range(1, concurrency), options => options.WithStrictOrdering(),
            "the shared counter must allocate a dense, gap-free 1..N run under real concurrency");
    }

    [Fact]
    public async Task Concurrent_allocations_for_two_different_series_on_the_same_day_never_collide()
    {
        var today = new DateOnly(2026, 9, 20);
        var quoteTask = Task.Run(async () =>
        {
            await using var db = _fixture.CreateContext();
            return await SequentialNumberAllocator.AllocateAsync(db, SequentialNumberAllocator.QuoteSeries, today, CancellationToken.None);
        });
        var productionTask = Task.Run(async () =>
        {
            await using var db = _fixture.CreateContext();
            return await SequentialNumberAllocator.AllocateAsync(db, SequentialNumberAllocator.ProductionOrderSeries, today, CancellationToken.None);
        });

        await Task.WhenAll(quoteTask, productionTask);
        (await quoteTask).Should().Be(1, "QUOTE and PRODUCTION_ORDER are independent series (ADR-0004 §1) — each starts its own run at 1");
        (await productionTask).Should().Be(1);
    }

    [Fact]
    public async Task A_rolled_back_transaction_never_leaves_a_gap_for_the_next_real_allocation()
    {
        var today = new DateOnly(2026, 9, 21);

        await using (var db = _fixture.CreateContext())
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var rolledBackSequence = await SequentialNumberAllocator.AllocateAsync(db, SequentialNumberAllocator.QuoteSeries, today, CancellationToken.None);
            rolledBackSequence.Should().Be(1);
            await tx.RollbackAsync();
        }

        await using var real = _fixture.CreateContext();
        var firstRealSequence = await SequentialNumberAllocator.AllocateAsync(real, SequentialNumberAllocator.QuoteSeries, today, CancellationToken.None);
        firstRealSequence.Should().Be(1, "the rolled-back attempt's increment must roll back with it — no gap for the next genuine allocation to inherit");
    }

    [Fact]
    public async Task Sequences_for_different_organization_days_never_collide_and_each_restarts_at_one()
    {
        await using var db = _fixture.CreateContext();
        var day1 = await SequentialNumberAllocator.AllocateAsync(db, SequentialNumberAllocator.QuoteSeries, new DateOnly(2026, 9, 22), CancellationToken.None);
        var day2 = await SequentialNumberAllocator.AllocateAsync(db, SequentialNumberAllocator.QuoteSeries, new DateOnly(2026, 9, 23), CancellationToken.None);
        var day1Again = await SequentialNumberAllocator.AllocateAsync(db, SequentialNumberAllocator.QuoteSeries, new DateOnly(2026, 9, 22), CancellationToken.None);

        day1.Should().Be(1);
        day2.Should().Be(1);
        day1Again.Should().Be(2);
    }
}
