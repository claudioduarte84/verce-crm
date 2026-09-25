using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Infrastructure.Marketplaces;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// ADR-0024 "Cleanup": "A failed deletion advances retry eligibility with exponential 1, 2, 4...
/// minute delay capped at one hour; later healthy rows therefore progress." This test constructs
/// one permanently-failing terminal operation (its physical candidate file's actual version never
/// matches what the DB row believes it staged — a deliberate, deterministic VERSION_CONFLICT,
/// distinct from the more common NOT_FOUND-is-treated-as-success case) directly against the real
/// store/DB (bypassing the HTTP flow, which has no route that produces this exact adversarial
/// state on demand), alongside several ordinary healthy due rows, and proves repeated sweeps let
/// the healthy rows finish while the bad one is retried with growing backoff and never blocks them.
/// REFRESH operations are used as the vehicle (rather than CONNECT_NEW/RECONNECT) purely because
/// they need only a real MarketplaceAccount row, not a full authorization session, to satisfy the
/// aggregate's own construction invariants and FK constraints — the fairness behavior under test
/// (CleanupTerminalAsync's due/keyset ordering and backoff) is identical for every operation kind.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MarketplaceHousekeepingFairnessTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MarketplaceTestHarness _harness = null!;
    private TestClock _clock = null!;
    private SalesChannel _channel = null!;

    public MarketplaceHousekeepingFairnessTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _harness = new MarketplaceTestHarness();
        _clock = new TestClock(DateTimeOffset.UtcNow);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, _harness.ExtraConfiguration, clockOverride: _clock, configureTestServices: _harness.ConfigureServices);
        await using var db = _fixture.CreateContext();
        if (!await db.Set<MarketplaceProvider>().AnyAsync(x => x.Code == "FAKEFAIR"))
            db.Add(new MarketplaceProvider("FAKEFAIR", "Fake Fairness Provider"));
        _channel = new SalesChannel("MKT-FAIR-" + Guid.NewGuid().ToString("N")[..8], "Canal Fairness", SalesChannelKind.Marketplace, null, null);
        db.Add(_channel);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _harness.Dispose();
        await using var db = _fixture.CreateContext();
        db.RemoveRange(await db.Set<MarketplaceAccountOperation>().Where(x => x.ProviderCode == "FAKEFAIR").ToListAsync());
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<MarketplaceAccount>().Where(x => x.ProviderCode == "FAKEFAIR").ToListAsync());
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<SalesChannel>().Where(x => x.Id == _channel.Id).ToListAsync());
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == "FAKEFAIR");
        if (provider is not null) db.Remove(provider);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task One_permanently_failing_cleanup_row_never_blocks_later_healthy_rows_from_progressing()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProtectedCredentialStore>();
        var workflow = scope.ServiceProvider.GetRequiredService<MarketplaceAuthorizationWorkflow>();
        var now = _clock.UtcNow;

        Guid badOperationRowId;
        await using (var db = _fixture.CreateContext())
        {
            var badAccount = new MarketplaceAccount("FAKEFAIR", "EXT-BAD-" + Guid.NewGuid().ToString("N")[..8], _channel.Id, "Bad account");
            db.Add(badAccount);
            // MarketplaceAccountOperation.Id is server-generated (UUID v7 on construction) — the
            // store artifacts below must be keyed by THIS row's actual Id, not a separately
            // invented Guid, or CleanupTerminalAsync's later DeleteCandidateAsync(op.Id, ...) call
            // would look for a candidate file that was never created under that Id (surfacing as
            // a spurious NOT_FOUND — itself treated as a successful deletion — instead of the
            // intended permanent VERSION_CONFLICT).
            var badOperation = new MarketplaceAccountOperation(MarketplaceAccountOperationKind.REFRESH, "FAKEFAIR", badAccount.Id, null, badAccount.Version, null, null, now);
            db.Add(badOperation);
            await db.SaveChangesAsync(); // materializes badOperation.Id
            badOperationRowId = badOperation.Id;

            // The oldest row (created first, so it sorts first under
            // OrderBy(NextCleanupAt).ThenBy(CreatedAt)) is deliberately unfixable: the DB row
            // claims candidate version N+1, but the physical candidate file the store actually
            // holds is version N — every delete attempt returns VERSION_CONFLICT, forever.
            var badReference = RandomReference();
            var badSessionId = Guid.NewGuid();
            var stagedBad = await store.CreateStagedAsync(new ProtectedCredentialWrite(badOperationRowId, badSessionId, badAccount.Id, "FAKEFAIR", badReference, Encoding.UTF8.GetBytes("bad")), CancellationToken.None);
            stagedBad.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
            var boundBad = await store.BindCandidateAsync(new ProtectedCredentialBinding(badOperationRowId, badSessionId, badAccount.Id, "FAKEFAIR", badReference), CancellationToken.None);
            boundBad.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);

            badOperation.MarkExternalInFlight(now);
            badOperation.RecordPersistedCandidate(badReference, stagedBad.Receipt!.CredentialVersion + 1, now); // deliberately WRONG version vs. the real file
            badOperation.FailClosed("FAIRNESS_TEST_BAD", now);
            badOperation.RequestCleanup(now);
            await db.SaveChangesAsync();
        }

        // Three ordinary healthy rows, created afterward (so they sort after the bad one), each
        // with a real candidate whose DB-claimed version matches the physical file exactly.
        var healthyIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            await using var db = _fixture.CreateContext();
            var account = new MarketplaceAccount("FAKEFAIR", "EXT-GOOD-" + i + "-" + Guid.NewGuid().ToString("N")[..6], _channel.Id, "Good account " + i);
            db.Add(account);
            var operationTime = now.AddMilliseconds(i + 1);
            var operation = new MarketplaceAccountOperation(MarketplaceAccountOperationKind.REFRESH, "FAKEFAIR", account.Id, null, account.Version, null, null, operationTime);
            db.Add(operation);
            await db.SaveChangesAsync(); // materializes operation.Id, used below as the real store key

            var reference = RandomReference();
            var sessionId = Guid.NewGuid();
            var healthyStaged = await store.CreateStagedAsync(new ProtectedCredentialWrite(operation.Id, sessionId, account.Id, "FAKEFAIR", reference, Encoding.UTF8.GetBytes("good-" + i)), CancellationToken.None);
            healthyStaged.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
            var boundGood = await store.BindCandidateAsync(new ProtectedCredentialBinding(operation.Id, sessionId, account.Id, "FAKEFAIR", reference), CancellationToken.None);
            boundGood.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
            operation.MarkExternalInFlight(operationTime);
            operation.RecordPersistedCandidate(reference, healthyStaged.Receipt!.CredentialVersion, operationTime);
            operation.FailClosed("FAIRNESS_TEST_GOOD", operationTime);
            operation.RequestCleanup(operationTime);
            await db.SaveChangesAsync();
            healthyIds.Add(operation.Id);
        }

        // Several sweeps, advancing the clock past each attempt's growing backoff each time.
        for (var sweep = 0; sweep < 4; sweep++)
        {
            await workflow.CleanupTerminalAsync(100, CancellationToken.None);
            _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        }

        await using var verify = _fixture.CreateContext();
        var bad = await verify.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == badOperationRowId);
        bad.CleanupState.Should().Be(MarketplaceAccountOperationCleanupState.PENDING, "the permanently-failing row must never be falsely marked done");
        bad.CleanupAttemptCount.Should().BeGreaterThan(0, "each failed attempt must advance retry eligibility");

        foreach (var id in healthyIds)
        {
            var healthy = await verify.Set<MarketplaceAccountOperation>().SingleAsync(x => x.Id == id);
            healthy.CleanupState.Should().Be(MarketplaceAccountOperationCleanupState.DONE,
                "healthy later rows must progress to completion despite an older permanently-failing row being swept in the same batches");
        }
    }

    private static string RandomReference() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
}
