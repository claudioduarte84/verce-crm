using System.Collections.Generic;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Verce.Infrastructure.Marketplaces;
using Verce.Modules.Commerce;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// ADR-0024 G-02 credential-store certification, exercised directly against
/// <see cref="LocalProtectedCredentialStore"/> (no HTTP host, no PostgreSQL — this is pure
/// filesystem/crypto behavior) using only real disposable temp directories, never the repository,
/// content root or wwwroot. No test here reads or logs decrypted secret bytes beyond comparing
/// them to a value THIS test itself wrote, and every directory is removed in <see cref="Dispose"/>.
/// </summary>
public sealed class LocalProtectedCredentialStoreTests : IDisposable
{
    private readonly List<string> _tempRoots = new();

    private (string Root, string KeyDir) NewRoots([System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        var root = Path.Combine(Path.GetTempPath(), "verce-store-test-" + name + "-" + Guid.NewGuid().ToString("N"));
        var keyDir = Path.Combine(Path.GetTempPath(), "verce-store-test-keys-" + name + "-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        _tempRoots.Add(keyDir);
        return (root, keyDir);
    }

    private static LocalProtectedCredentialStore NewStore(string root, string keyDir, string? repositoryRoot = null, string? contentRoot = null, string? webRoot = null)
    {
        var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Directory.CreateDirectory(keyDir).FullName));
        var options = Options.Create(new MarketplaceCredentialStoreOptions { CredentialStoreRoot = root, RepositoryRoot = repositoryRoot });
        var environment = new FakeWebHostEnvironment
        {
            ContentRootPath = contentRoot ?? Path.Combine(Path.GetTempPath(), "verce-unused-content-root-" + Guid.NewGuid().ToString("N")),
            WebRootPath = webRoot ?? Path.Combine(Path.GetTempPath(), "verce-unused-web-root-" + Guid.NewGuid().ToString("N")),
        };
        return new LocalProtectedCredentialStore(dataProtection, options, environment);
    }

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void Valid_external_root_outside_repo_content_and_wwwroot_is_accepted()
    {
        var (root, keyDir) = NewRoots();
        using var store = NewStore(root, keyDir);
        Directory.Exists(root).Should().BeTrue();
    }

    [Fact]
    public void Root_equal_to_repository_root_is_rejected()
    {
        var (root, keyDir) = NewRoots();
        var act = () => NewStore(root, keyDir, repositoryRoot: root);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Root_inside_repository_root_is_rejected()
    {
        var (root, keyDir) = NewRoots();
        var repoRoot = Path.GetDirectoryName(root)!;
        var act = () => NewStore(root, keyDir, repositoryRoot: repoRoot);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Repository_root_inside_the_configured_store_root_is_also_rejected()
    {
        var (root, keyDir) = NewRoots();
        Directory.CreateDirectory(root);
        var nestedRepoRoot = Path.Combine(root, "repo");
        var act = () => NewStore(root, keyDir, repositoryRoot: nestedRepoRoot);
        act.Should().Throw<InvalidOperationException>("overlap must be rejected in both directions, not just root-inside-repo");
    }

    [Fact]
    public void Root_inside_content_root_is_rejected()
    {
        var (root, keyDir) = NewRoots();
        var contentRoot = Path.GetDirectoryName(root)!;
        var act = () => NewStore(root, keyDir, contentRoot: contentRoot);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Root_inside_wwwroot_is_rejected()
    {
        var (root, keyDir) = NewRoots();
        var webRoot = Path.GetDirectoryName(root)!;
        var act = () => NewStore(root, keyDir, webRoot: webRoot);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Relative_root_path_is_rejected()
    {
        var (_, keyDir) = NewRoots();
        var act = () => NewStore("relative/not/absolute", keyDir);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Atomic_write_then_bind_then_promote_round_trips_exact_bytes_with_a_stable_receipt()
    {
        var (root, keyDir) = NewRoots();
        using var store = NewStore(root, keyDir);
        var operationId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var reference = RandomReference();
        var secret = Encoding.UTF8.GetBytes("super-secret-token-value");

        var staged = await store.CreateStagedAsync(new ProtectedCredentialWrite(operationId, sessionId, accountId, "FAKE", reference, secret), CancellationToken.None);
        staged.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        staged.Receipt!.CredentialVersion.Should().Be(1);
        staged.Receipt.IntegrityEvidence.Should().MatchRegex("^[0-9a-f]{64}$", "integrity evidence is a SHA-256 hex digest");

        var bound = await store.BindCandidateAsync(new ProtectedCredentialBinding(operationId, sessionId, accountId, "FAKE", reference), CancellationToken.None);
        bound.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);

        var promoted = await store.PromoteCandidateAsync(operationId, reference, 1, CancellationToken.None);
        promoted.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);

        var read = await store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, "FAKE", reference, 1, operationId), CancellationToken.None);
        read.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        read.SecretMaterial.ToArray().Should().Equal(secret);
    }

    [Fact]
    public async Task Candidate_with_a_valid_receipt_but_no_promotion_is_never_returned_as_confirmed()
    {
        // "Candidate isolation": a persisted candidate that has a real store receipt but whose
        // operation is not (yet) CONFIRMED must never be readable through the normal execution
        // path — UseConfirmedAsync only ever reads the CONFIRMED path, never staged/candidate.
        var (root, keyDir) = NewRoots();
        using var store = NewStore(root, keyDir);
        var operationId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var reference = RandomReference();
        var secret = Encoding.UTF8.GetBytes("never-should-be-readable");
        var sessionId = Guid.NewGuid();

        var staged = await store.CreateStagedAsync(new ProtectedCredentialWrite(operationId, sessionId, accountId, "FAKE", reference, secret), CancellationToken.None);
        staged.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        var bound = await store.BindCandidateAsync(new ProtectedCredentialBinding(operationId, sessionId, accountId, "FAKE", reference), CancellationToken.None);
        bound.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);

        // Never promoted. A normal confirmed read must see nothing — the candidate has a real,
        // valid receipt, but its operation is not CONFIRMED, so it is not executable.
        var read = await store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, "FAKE", reference, staged.Receipt!.CredentialVersion, operationId), CancellationToken.None);
        read.Outcome.Should().Be(ProtectedCredentialStoreOutcome.NOT_FOUND);
    }

    [Fact]
    public async Task CompareAndSwap_succeeds_on_exact_expected_version_and_conflicts_otherwise()
    {
        var (root, keyDir) = NewRoots();
        using var store = NewStore(root, keyDir);
        var accountId = Guid.NewGuid();
        var reference = RandomReference();
        var op2 = Guid.NewGuid();
        var session2 = Guid.NewGuid();
        var staged = await store.CreateStagedAsync(new ProtectedCredentialWrite(op2, session2, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("v1")), CancellationToken.None);
        await store.BindCandidateAsync(new ProtectedCredentialBinding(op2, session2, accountId, "FAKE", reference), CancellationToken.None);
        await store.PromoteCandidateAsync(op2, reference, staged.Receipt!.CredentialVersion, CancellationToken.None);

        var casWrongVersion = await store.CompareAndSwapReplaceAsync(new ProtectedCredentialWrite(Guid.NewGuid(), null, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("v2")), expectedVersion: 99, CancellationToken.None);
        casWrongVersion.Outcome.Should().Be(ProtectedCredentialStoreOutcome.VERSION_CONFLICT);

        var casRight = await store.CompareAndSwapReplaceAsync(new ProtectedCredentialWrite(Guid.NewGuid(), null, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("v2")), expectedVersion: staged.Receipt.CredentialVersion, CancellationToken.None);
        casRight.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        casRight.Receipt!.CredentialVersion.Should().Be(staged.Receipt.CredentialVersion + 1);

        // A second CAS with the NOW-stale expected version must conflict — proves the store owns
        // the authoritative version, and Commerce's own last-confirmed belief is never silently trusted.
        var casStaleAgain = await store.CompareAndSwapReplaceAsync(new ProtectedCredentialWrite(Guid.NewGuid(), null, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("v3")), expectedVersion: staged.Receipt.CredentialVersion, CancellationToken.None);
        casStaleAgain.Outcome.Should().Be(ProtectedCredentialStoreOutcome.VERSION_CONFLICT);
    }

    [Fact]
    public async Task Concurrent_CAS_attempts_at_the_same_expected_version_have_exactly_one_winner()
    {
        var (root, keyDir) = NewRoots();
        using var store = NewStore(root, keyDir);
        var accountId = Guid.NewGuid();
        var reference = RandomReference();
        var op = Guid.NewGuid();
        var session = Guid.NewGuid();
        var staged = await store.CreateStagedAsync(new ProtectedCredentialWrite(op, session, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("v1")), CancellationToken.None);
        await store.BindCandidateAsync(new ProtectedCredentialBinding(op, session, accountId, "FAKE", reference), CancellationToken.None);
        await store.PromoteCandidateAsync(op, reference, staged.Receipt!.CredentialVersion, CancellationToken.None);

        var attempts = Enumerable.Range(0, 8).Select(i => store.CompareAndSwapReplaceAsync(
            new ProtectedCredentialWrite(Guid.NewGuid(), null, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("race-" + i)),
            expectedVersion: staged.Receipt.CredentialVersion, CancellationToken.None));
        var results = await Task.WhenAll(attempts);

        results.Count(r => r.Outcome == ProtectedCredentialStoreOutcome.SUCCESS).Should().Be(1, "the per-reference gate must serialize concurrent CAS attempts to exactly one winner");
        results.Count(r => r.Outcome == ProtectedCredentialStoreOutcome.VERSION_CONFLICT).Should().Be(7);
    }

    [Fact]
    public async Task Many_concurrent_reads_during_a_replace_never_see_a_torn_or_missing_file()
    {
        var (root, keyDir) = NewRoots();
        using var store = NewStore(root, keyDir);
        var accountId = Guid.NewGuid();
        var reference = RandomReference();
        var op = Guid.NewGuid();
        var session = Guid.NewGuid();
        var staged = await store.CreateStagedAsync(new ProtectedCredentialWrite(op, session, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("before")), CancellationToken.None);
        await store.BindCandidateAsync(new ProtectedCredentialBinding(op, session, accountId, "FAKE", reference), CancellationToken.None);
        await store.PromoteCandidateAsync(op, reference, staged.Receipt!.CredentialVersion, CancellationToken.None);
        var confirmedVersion = staged.Receipt.CredentialVersion;

        var replaceTask = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
                await store.CompareAndSwapReplaceAsync(new ProtectedCredentialWrite(Guid.NewGuid(), null, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("after-" + i)), confirmedVersion + i, CancellationToken.None);
        });
        var readTasks = Enumerable.Range(0, 50).Select(async _ =>
        {
            // A concurrent reader does not know the exact evolving version, so it reads by
            // scanning outcomes only: it must NEVER throw, and NEVER see CORRUPTED/torn content —
            // only SUCCESS (with fully valid bytes) or a clean VERSION_CONFLICT against a stale guess.
            var outcome = await store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, "FAKE", reference, confirmedVersion, op), CancellationToken.None);
            return outcome.Outcome;
        });
        var readOutcomes = await Task.WhenAll(readTasks);
        await replaceTask;

        readOutcomes.Should().OnlyContain(o => o == ProtectedCredentialStoreOutcome.SUCCESS || o == ProtectedCredentialStoreOutcome.VERSION_CONFLICT,
            "a reader must always see a fully-written old or new file, never a torn write or an unexpected exception surfaced as another outcome");
    }

    [Fact]
    public async Task Corrupt_ciphertext_is_reported_as_corrupted_not_thrown_or_misread()
    {
        var (root, keyDir) = NewRoots();
        using var store = NewStore(root, keyDir);
        var accountId = Guid.NewGuid();
        var reference = RandomReference();
        var op = Guid.NewGuid();
        var session = Guid.NewGuid();
        var staged = await store.CreateStagedAsync(new ProtectedCredentialWrite(op, session, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("v1")), CancellationToken.None);
        await store.BindCandidateAsync(new ProtectedCredentialBinding(op, session, accountId, "FAKE", reference), CancellationToken.None);
        await store.PromoteCandidateAsync(op, reference, staged.Receipt!.CredentialVersion, CancellationToken.None);

        var confirmedPath = Directory.GetFiles(root, "*.bin").Single(f => !f.Contains("staged") && !f.Contains("candidates"));
        await File.WriteAllBytesAsync(confirmedPath, Encoding.UTF8.GetBytes("garbage-not-a-real-dataprotection-payload"));

        var read = await store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, "FAKE", reference, staged.Receipt.CredentialVersion, op), CancellationToken.None);
        read.Outcome.Should().Be(ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE);
    }

    [Fact]
    public async Task Missing_file_is_reported_as_not_found()
    {
        var (root, keyDir) = NewRoots();
        using var store = NewStore(root, keyDir);
        var read = await store.UseConfirmedAsync(new ProtectedCredentialRead(Guid.NewGuid(), "FAKE", RandomReference(), 1, Guid.NewGuid()), CancellationToken.None);
        read.Outcome.Should().Be(ProtectedCredentialStoreOutcome.NOT_FOUND);
    }

    [Fact]
    public async Task Missing_data_protection_key_makes_previously_written_material_undecryptable()
    {
        var (root, keyDir) = NewRoots();
        var accountId = Guid.NewGuid();
        var reference = RandomReference();
        var op = Guid.NewGuid();
        var session = Guid.NewGuid();
        long version;
        using (var store = NewStore(root, keyDir))
        {
            var staged = await store.CreateStagedAsync(new ProtectedCredentialWrite(op, session, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("v1")), CancellationToken.None);
            await store.BindCandidateAsync(new ProtectedCredentialBinding(op, session, accountId, "FAKE", reference), CancellationToken.None);
            await store.PromoteCandidateAsync(op, reference, staged.Receipt!.CredentialVersion, CancellationToken.None);
            version = staged.Receipt.CredentialVersion;
        }

        // A fresh key ring (simulating key-ring loss) can never decrypt the old ciphertext.
        var freshKeyDir = Path.Combine(Path.GetTempPath(), "verce-store-test-fresh-keys-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(freshKeyDir);
        using var storeWithLostKeys = NewStore(root, freshKeyDir);
        var read = await storeWithLostKeys.UseConfirmedAsync(new ProtectedCredentialRead(accountId, "FAKE", reference, version, op), CancellationToken.None);
        read.Outcome.Should().Be(ProtectedCredentialStoreOutcome.CORRUPTED_OR_UNDECRYPTABLE);
    }

    [Fact]
    public async Task Restart_with_the_same_root_and_key_ring_reads_stable_identical_bytes()
    {
        var (root, keyDir) = NewRoots();
        var accountId = Guid.NewGuid();
        var reference = RandomReference();
        var op = Guid.NewGuid();
        var session = Guid.NewGuid();
        var secret = Encoding.UTF8.GetBytes("stable-across-restart");
        long version;
        using (var store = NewStore(root, keyDir))
        {
            var staged = await store.CreateStagedAsync(new ProtectedCredentialWrite(op, session, accountId, "FAKE", reference, secret), CancellationToken.None);
            await store.BindCandidateAsync(new ProtectedCredentialBinding(op, session, accountId, "FAKE", reference), CancellationToken.None);
            await store.PromoteCandidateAsync(op, reference, staged.Receipt!.CredentialVersion, CancellationToken.None);
            version = staged.Receipt.CredentialVersion;
        } // store (and its exclusive root ownership handle) disposed here — a real restart.

        using var restarted = NewStore(root, keyDir);
        var read = await restarted.UseConfirmedAsync(new ProtectedCredentialRead(accountId, "FAKE", reference, version, op), CancellationToken.None);
        read.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);
        read.SecretMaterial.ToArray().Should().Equal(secret);
    }

    [Fact]
    public void A_second_process_cannot_open_the_same_root_while_the_first_still_owns_it()
    {
        var (root, keyDir) = NewRoots();
        using var first = NewStore(root, keyDir);
        var act = () => NewStore(root, keyDir);
        act.Should().Throw<IOException>("the exclusive root ownership handle must refuse a second concurrent owner");
    }

    [Fact]
    public async Task Version_mismatch_on_read_is_a_conflict_even_when_newer_material_physically_exists()
    {
        // "The connection owns only the last CONFIRMED version": a caller asking for an outdated
        // version it still believes is current must get an explicit conflict, never silently the
        // newest bytes — Commerce's own truth must be updated through the normal operation-arbiter
        // path, never inferred from "whatever the store happens to have now."
        var (root, keyDir) = NewRoots();
        using var store = NewStore(root, keyDir);
        var accountId = Guid.NewGuid();
        var reference = RandomReference();
        var op1 = Guid.NewGuid();
        var session1 = Guid.NewGuid();
        var staged = await store.CreateStagedAsync(new ProtectedCredentialWrite(op1, session1, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("v1")), CancellationToken.None);
        await store.BindCandidateAsync(new ProtectedCredentialBinding(op1, session1, accountId, "FAKE", reference), CancellationToken.None);
        await store.PromoteCandidateAsync(op1, reference, staged.Receipt!.CredentialVersion, CancellationToken.None);
        var v1 = staged.Receipt.CredentialVersion;

        var cas = await store.CompareAndSwapReplaceAsync(new ProtectedCredentialWrite(Guid.NewGuid(), null, accountId, "FAKE", reference, Encoding.UTF8.GetBytes("v2")), v1, CancellationToken.None);
        cas.Outcome.Should().Be(ProtectedCredentialStoreOutcome.SUCCESS);

        var staleRead = await store.UseConfirmedAsync(new ProtectedCredentialRead(accountId, "FAKE", reference, v1, op1), CancellationToken.None);
        staleRead.Outcome.Should().Be(ProtectedCredentialStoreOutcome.VERSION_CONFLICT);
    }

    private static string RandomReference() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Verce.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
    }
}
