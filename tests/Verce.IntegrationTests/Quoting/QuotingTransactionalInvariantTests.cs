using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Pricing;
using Verce.Modules.Production;
using Verce.Modules.Quoting;
using Verce.Platform.Persistence;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Quoting;

/// <summary>
/// ADR-0012 §2 / ADR-0020 §A.8: "no APPROVED revision may exist without its required
/// ProductionOrder invariant being satisfied" — proven here by deliberately reaching Production's
/// handler with an in-flight previous order (bypassing the composition-root's own guard, which
/// the HTTP-level test in <see cref="QuotingHttpIntegrationTests"/> already proves rejects this
/// BEFORE the transaction starts) to force the handler's defensive exception and confirm the
/// WHOLE Unit of Work — including the revision's own APPROVED transition — rolls back. Also
/// covers <see cref="ExpireQuotesService"/> against a real database with a controlled clock.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QuotingTransactionalInvariantTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;

    public QuotingTransactionalInvariantTests(PostgresFixture fixture) => _fixture = fixture;

    private static QuoteItemSnapshot TestItem(string name, Guid salesChannelId) => new(
        null, null, null, name, null, 1m, new QuoteItemCostSnapshotInput("MANUAL", 0m, 0m, 0m, null, null, null, 0m, null, null, 0m, 0m, 10m, 1, 10m, [], []),
        0.30m, salesChannelId, null, 0m, QuoteFixedFeeApplication.PerUnit, 0m, 0m, "CENT", 20m, 0m, null, null, QuoteDiscountKind.None, 0m);

    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE quoting.quote_status_history, quoting.quote_item, quoting.quote_revision, quoting.quote,
                           quoting.quote_number_counter, production.production_order,
                           pricing.fee_rule_version, pricing.fee_rule, pricing.sales_channel,
                           settings.app_setting, platform.account_setup_token, platform.user_role,
                           platform.user_claim, platform.user_login, platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Settings:SeedOnStartup"] = "true",
        });
        using var warmup = _factory.CreateHttpsClient();
        (await warmup.GetAsync("/health/live")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Approval_transaction_rolls_back_entirely_when_the_production_invariant_cannot_be_established()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var direct = await db.Set<SalesChannel>().AsNoTracking().SingleAsync(x => x.Code == "DIRECT");
        var today = clock.OrganizationToday();

        // R1: create and approve normally (real order, QUEUED).
        Guid quoteId;
        await uow.ExecuteAsync(async (ctx, ct) =>
        {
            var item = TestItem("Item", direct.Id);
            var quote = new Verce.Modules.Quoting.Quote(1, today, new CustomerSnapshotInput(null, null, null, null, null),
                direct.Id, [item], 15, null, Guid.NewGuid(), clock.UtcNow);
            ctx.Add(quote);
            quoteId = quote.Id;
            await Task.CompletedTask;
        });
        quoteId = await db.Set<Verce.Modules.Quoting.Quote>().AsNoTracking().Select(q => q.Id).SingleAsync();

        await uow.ExecuteAsync(async (ctx, ct) =>
        {
            var quote = await ctx.Set<Verce.Modules.Quoting.Quote>().Include(x => x.Revisions).ThenInclude(x => x.Items)
                .Include(x => x.Revisions).ThenInclude(x => x.History).SingleAsync(x => x.Id == quoteId, ct);
            quote.Approve(null, Guid.NewGuid(), clock.UtcNow, today, allowDirectApproval: true);
        });

        var r1RevisionId = await db.Set<Verce.Modules.Quoting.QuoteRevision>().AsNoTracking()
            .Where(r => r.QuoteId == quoteId).Select(r => r.Id).SingleAsync();

        // Flip R1's order to IN_PRODUCTION directly (S9 hasn't shipped this transition yet).
        // Deliberately done through the SAME scoped `db`/`uow` this test reuses throughout —
        // EF's identity map would otherwise keep serving R2's later query the stale, already-
        // tracked QUEUED instance from R1's own creation above if a second, independent
        // DbContext mutated the row out of band.
        {
            var order = await db.Set<ProductionOrder>().SingleAsync(o => o.QuoteRevisionId == r1RevisionId);
            typeof(ProductionOrder).GetProperty(nameof(ProductionOrder.Status))!.SetValue(order, ProductionOrderStatus.IN_PRODUCTION);
            await db.SaveChangesAsync();
        }

        // Construct R2 (always allowed) and approve it WITHOUT the composition root's guard —
        // reaching Production's handler with an in-flight previous order, which must throw and
        // roll back the ENTIRE transaction, including R2's own APPROVED transition.
        Guid r2Id;
        await uow.ExecuteAsync(async (ctx, ct) =>
        {
            var quote = await ctx.Set<Verce.Modules.Quoting.Quote>().Include(x => x.Revisions).ThenInclude(x => x.Items)
                .Include(x => x.Revisions).ThenInclude(x => x.History).SingleAsync(x => x.Id == quoteId, ct);
            var item = TestItem("Item R2", direct.Id);
            var r2 = quote.ConstructNextRevision(new CustomerSnapshotInput(null, null, null, null, null), direct.Id,
                [item], 15, today, null, Guid.NewGuid(), clock.UtcNow);
            r2Id = r2.Id;
        });
        r2Id = await db.Set<Verce.Modules.Quoting.QuoteRevision>().AsNoTracking()
            .Where(r => r.QuoteId == quoteId && r.RevisionIndex == 2).Select(r => r.Id).SingleAsync();

        var act = async () => await uow.ExecuteAsync(async (ctx, ct) =>
        {
            var quote = await ctx.Set<Verce.Modules.Quoting.Quote>().Include(x => x.Revisions).ThenInclude(x => x.Items)
                .Include(x => x.Revisions).ThenInclude(x => x.History).SingleAsync(x => x.Id == quoteId, ct);
            quote.Approve(null, Guid.NewGuid(), clock.UtcNow, today, allowDirectApproval: true);
        });
        // H-05: the SAME stable code/exception type as the composition root's pre-check —
        // never a different, differently-typed error for a race that reaches the authoritative
        // in-transaction guard instead of the optimization pre-check.
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("PRODUCTION_ORDER_IN_PROGRESS");

        // The WHOLE transaction rolled back: R2 is still GENERATED (never APPROVED), and no
        // ProductionOrder exists for it — the invariant holds even when reached the wrong way.
        await using (var verify = _fixture.CreateContext())
        {
            var r2After = await verify.Set<Verce.Modules.Quoting.QuoteRevision>().AsNoTracking().SingleAsync(r => r.Id == r2Id);
            r2After.Status.Should().Be(QuoteRevisionStatus.GENERATED, "the failed approval must roll back completely");
            (await verify.Set<ProductionOrder>().AnyAsync(o => o.QuoteRevisionId == r2Id)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task ExpireQuotesService_expires_only_eligible_current_revisions_and_is_idempotent()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var direct = await db.Set<SalesChannel>().AsNoTracking().SingleAsync(x => x.Code == "DIRECT");
        var today = clock.OrganizationToday();

        // Quote A: 1-day validity, becomes eligible "tomorrow" — created with ValidUntil already
        // in the past relative to "today + 2" (simulated below via a fixed clock in the service).
        Guid quoteAId, quoteBId;
        await uow.ExecuteAsync(async (ctx, ct) =>
        {
            var item = TestItem("Item", direct.Id);
            var quoteA = new Verce.Modules.Quoting.Quote(1, today, new CustomerSnapshotInput(null, null, null, null, null),
                direct.Id, [item], 1, null, Guid.NewGuid(), clock.UtcNow); // 1-day validity: expires tomorrow
            var quoteB = new Verce.Modules.Quoting.Quote(2, today, new CustomerSnapshotInput(null, null, null, null, null),
                direct.Id, [item], 365, null, Guid.NewGuid(), clock.UtcNow); // long validity: must NOT expire
            ctx.Add(quoteA);
            ctx.Add(quoteB);
            await Task.CompletedTask;
        });
        var ids = await db.Set<Verce.Modules.Quoting.Quote>().AsNoTracking().OrderBy(q => q.NumberSequence).Select(q => q.Id).ToListAsync();
        quoteAId = ids[0];
        quoteBId = ids[1];

        var futureClock = new FixedClock(clock.UtcNow.AddDays(3), clock.OrganizationTimeZoneId);
        var futureService = new ExpireQuotesService(db, uow, futureClock);

        var expiredCount = await futureService.ExpireEligibleAsync(CancellationToken.None);
        expiredCount.Should().Be(1);

        await using (var verify = _fixture.CreateContext())
        {
            var quoteA = await verify.Set<Verce.Modules.Quoting.Quote>().Include(x => x.Revisions).AsNoTracking().SingleAsync(q => q.Id == quoteAId);
            quoteA.CurrentRevision.Status.Should().Be(QuoteRevisionStatus.EXPIRED);

            var quoteB = await verify.Set<Verce.Modules.Quoting.Quote>().Include(x => x.Revisions).AsNoTracking().SingleAsync(q => q.Id == quoteBId);
            quoteB.CurrentRevision.Status.Should().Be(QuoteRevisionStatus.GENERATED, "a revision still within its validity window must never expire");
        }

        // Idempotent: re-running changes nothing (quote A already expired, nothing else eligible).
        var secondRun = await futureService.ExpireEligibleAsync(CancellationToken.None);
        secondRun.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(ExpireQuotesService.MaxBatchSize)]
    public async Task ExpireQuotesService_accepts_every_valid_batch_size_including_the_technical_maximum(int batchSize)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var service = new ExpireQuotesService(db, uow, clock);

        // An empty eligible set is enough to prove the value is ACCEPTED (never throws) — F-04's
        // unit-level tests already prove the exact boundary rejection for invalid values, and
        // populating 1000 real eligible quotes here would only slow this down for no extra signal.
        var act = async () => await service.ExpireEligibleAsync(CancellationToken.None, batchSize);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExpireQuotesService_bounds_the_eligibility_query_to_the_requested_batch_size()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var direct = await db.Set<SalesChannel>().AsNoTracking().SingleAsync(x => x.Code == "DIRECT");
        var today = clock.OrganizationToday();

        // Three quotes with staggered validity, so the deterministic ValidUntil/QuoteId ordering
        // gives a predictable "which two win the first batch" answer.
        await uow.ExecuteAsync(async (ctx, ct) =>
        {
            var item = TestItem("Item", direct.Id);
            foreach (var validityDays in new[] { 1, 2, 3 })
            {
                ctx.Add(new Verce.Modules.Quoting.Quote(validityDays, today, new CustomerSnapshotInput(null, null, null, null, null),
                    direct.Id, [item], validityDays, null, Guid.NewGuid(), clock.UtcNow));
            }
            await Task.CompletedTask;
        });

        var futureClock = new FixedClock(clock.UtcNow.AddDays(30), clock.OrganizationTimeZoneId);
        var futureService = new ExpireQuotesService(db, uow, futureClock);

        var firstBatch = await futureService.ExpireEligibleAsync(CancellationToken.None, batchSize: 2);
        firstBatch.Should().Be(2, "H-02: one sweep must never process more than its bounded batch size");

        await using (var verify = _fixture.CreateContext())
        {
            var revisions = await verify.Set<Verce.Modules.Quoting.QuoteRevision>().AsNoTracking().ToListAsync();
            revisions.Count(r => r.Status == QuoteRevisionStatus.EXPIRED).Should().Be(2);
            revisions.Count(r => r.Status == QuoteRevisionStatus.GENERATED).Should().Be(1, "the third quote must be left for a later batch, not silently skipped forever");
        }

        var secondBatch = await futureService.ExpireEligibleAsync(CancellationToken.None, batchSize: 2);
        secondBatch.Should().Be(1, "the remaining eligible quote is drained on the next scheduled run");

        await using (var verify = _fixture.CreateContext())
        {
            (await verify.Set<Verce.Modules.Quoting.QuoteRevision>().AsNoTracking().CountAsync(r => r.Status == QuoteRevisionStatus.EXPIRED)).Should().Be(3);
        }
    }

    [Fact]
    public async Task ExpireQuotesService_isolates_a_concurrent_conflict_to_one_quote_and_never_double_counts_or_aborts_the_sweep()
    {
        using var seedScope = _factory.Services.CreateScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<VerceDbContext>();
        var seedUow = seedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = seedScope.ServiceProvider.GetRequiredService<IClock>();
        var direct = await seedDb.Set<SalesChannel>().AsNoTracking().SingleAsync(x => x.Code == "DIRECT");
        var today = clock.OrganizationToday();

        await seedUow.ExecuteAsync(async (ctx, ct) =>
        {
            var item = TestItem("Item", direct.Id);
            for (var i = 1; i <= 3; i++)
            {
                ctx.Add(new Verce.Modules.Quoting.Quote(i, today, new CustomerSnapshotInput(null, null, null, null, null),
                    direct.Id, [item], 1, null, Guid.NewGuid(), clock.UtcNow));
            }
            await Task.CompletedTask;
        });

        var futureUtcNow = clock.UtcNow.AddDays(30);

        // Two INDEPENDENT scopes (own VerceDbContext/UnitOfWork each) racing over the SAME three
        // eligible quotes — proves a version conflict on one quote (whichever the loser reaches
        // after the other has already committed it) is caught and isolated: it never aborts that
        // sweep's processing of the OTHER quotes, and the optimistic-concurrency version column
        // guarantees exactly one of the two sweeps ever counts a given quote's expiration — the
        // combined total across both calls must equal exactly 3, never less (a lost update) and
        // never more (a double count).
        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();
        var service1 = new ExpireQuotesService(scope1.ServiceProvider.GetRequiredService<VerceDbContext>(),
            scope1.ServiceProvider.GetRequiredService<IUnitOfWork>(), new FixedClock(futureUtcNow, clock.OrganizationTimeZoneId));
        var service2 = new ExpireQuotesService(scope2.ServiceProvider.GetRequiredService<VerceDbContext>(),
            scope2.ServiceProvider.GetRequiredService<IUnitOfWork>(), new FixedClock(futureUtcNow, clock.OrganizationTimeZoneId));

        var racingTask1 = service1.ExpireEligibleAsync(CancellationToken.None);
        var racingTask2 = service2.ExpireEligibleAsync(CancellationToken.None);
        var act = async () => await Task.WhenAll(racingTask1, racingTask2);
        await act.Should().NotThrowAsync("a DbUpdateConcurrencyException on one quote must never escape the sweep");

        var combinedCounts = await Task.WhenAll(racingTask1, racingTask2);
        combinedCounts.Sum().Should().Be(3,
            "the version column guarantees exactly one sweep ever counts a given quote's expiration — never lost, never doubled");

        await using var verify = _fixture.CreateContext();
        (await verify.Set<Verce.Modules.Quoting.QuoteRevision>().AsNoTracking().CountAsync(r => r.Status == QuoteRevisionStatus.EXPIRED)).Should().Be(3);
    }

    /// <summary>Deterministic <see cref="IClock"/> for the expiration boundary test — the real
    /// production America/Sao_Paulo conversion, just pinned to a fixed instant.</summary>
    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow, string organizationTimeZoneId)
        {
            UtcNow = utcNow;
            OrganizationTimeZoneId = organizationTimeZoneId;
        }

        public DateTimeOffset UtcNow { get; }
        public string OrganizationTimeZoneId { get; }
        public DateOnly OrganizationToday() => OrganizationTimeZone.ToOrganizationDate(UtcNow, OrganizationTimeZoneId);
    }
}
