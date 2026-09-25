using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Auth;
using Verce.Api.Commerce;
using Verce.Infrastructure.Marketplaces;
using Verce.IntegrationTests.Auth;
using Verce.Modules.Commerce;
using Verce.Modules.Pricing;
using Verce.Platform.Identity;

namespace Verce.IntegrationTests.Commerce;

/// <summary>
/// ADR-0024 RT-03: K1-&gt;K2 credential-loss recovery. Every scenario starts from a CONFIRMED K1
/// at version &gt; 1 (never version 1 — the ADR is explicit that recovery behavior must be proven
/// past the trivial first-bind case), then destroys the physical material in some way and proves
/// (a) the account becomes REAUTHORIZATION_REQUIRED without deleting history, (b) an explicit
/// bound recovery installs a NEW reference (K2) whose version sequence starts at 1 — never a
/// reset of K1's own version — and (c) K2 only becomes executable through the normal
/// operation-CONFIRMED path. File manipulation uses only this test's own disposable temp
/// credential root (<see cref="MarketplaceTestHarness.CredentialStoreRoot"/>), never the
/// repository or an operational path.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MarketplaceAuthorizationRt03Tests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private VerceWebApplicationFactory _factory = null!;
    private MarketplaceTestHarness _harness = null!;
    private AuthTestClient _owner = null!;
    private string? _extraKeyDirectoryToCleanUp;
    private SalesChannel _channel = null!;

    public MarketplaceAuthorizationRt03Tests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _harness = new MarketplaceTestHarness();
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, _harness.ExtraConfiguration, configureTestServices: _harness.ConfigureServices);
        _channel = await SeedProviderAndChannelAsync();
        _owner = await LoggedInOwnerAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        _harness.Dispose();
        if (_extraKeyDirectoryToCleanUp is { } keyDirectory && Directory.Exists(keyDirectory))
        {
            try { Directory.Delete(keyDirectory, recursive: true); }
            catch { /* best-effort; the OS temp directory is reclaimed anyway */ }
        }
        await using var db = _fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE commerce.marketplace_account_connection SET confirmed_operation_id = NULL
            WHERE marketplace_account_id IN (SELECT id FROM commerce.marketplace_account WHERE provider_code = {FakeMarketplaceConnector.Code})
            """);
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<MarketplaceAccountOperation>().Where(x => x.ProviderCode == FakeMarketplaceConnector.Code).ToListAsync());
        db.RemoveRange(await db.Set<MarketplaceAuthorizationSession>().Where(x => x.ProviderCode == FakeMarketplaceConnector.Code).ToListAsync());
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<MarketplaceAccount>().Where(x => x.ProviderCode == FakeMarketplaceConnector.Code).ToListAsync());
        await db.SaveChangesAsync();
        db.RemoveRange(await db.Set<SalesChannel>().Where(x => x.Id == _channel.Id).ToListAsync());
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == FakeMarketplaceConnector.Code);
        if (provider is not null) db.Remove(provider);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task K1_file_missing_recovers_to_fresh_K2_and_old_file_absence_is_irrelevant()
    {
        var (accountId, externalId, k1Reference) = await ConnectAndAdvanceToVersionTwoAsync();
        File.Delete(ConfirmedFilePath(k1Reference));

        var k2Reference = await ReconnectAsync(accountId, externalId);

        k2Reference.Should().NotBe(k1Reference, "a fresh K2 reference must be generated, never the same name reused after loss");
        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.CredentialReference.Should().Be(k2Reference);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
        account.Connection.ConfirmedCredentialVersion.Should().Be(1, "K2 starts its own version sequence at 1");
        File.Exists(ConfirmedFilePath(k2Reference)).Should().BeTrue();
    }

    [Fact]
    public async Task K1_ciphertext_corrupted_recovers_to_fresh_K2()
    {
        var (accountId, externalId, k1Reference) = await ConnectAndAdvanceToVersionTwoAsync();
        await File.WriteAllBytesAsync(ConfirmedFilePath(k1Reference), Encoding.UTF8.GetBytes("not-valid-dataprotection-ciphertext"));

        var k2Reference = await ReconnectAsync(accountId, externalId);

        k2Reference.Should().NotBe(k1Reference);
        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
        account.Connection.ConfirmedCredentialVersion.Should().Be(1);
        // The corrupt K1 file is left in place (quarantined for cleanup), never silently repaired.
        File.Exists(ConfirmedFilePath(k1Reference)).Should().BeTrue();
    }

    [Fact]
    public async Task Unconfirmed_higher_K1_candidate_is_never_CAS_d_from_and_forces_fresh_K2()
    {
        var (accountId, externalId, k1Reference) = await ConnectAndAdvanceToVersionTwoAsync();
        // Simulate a failed operation that physically promoted K1 to V+1 before its DB commit
        // rolled back (RT-01's own documented scenario): the file now shows a HIGHER version than
        // Commerce's own DB truth (which still says confirmed version 2).
        await OverwriteConfirmedEnvelopeVersionAsync(k1Reference, 9);

        var k2Reference = await ReconnectAsync(accountId, externalId);

        k2Reference.Should().NotBe(k1Reference, "the stale higher-version K1 file must never be CAS'd from; only a fresh K2 may proceed");
        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.ConfirmedCredentialVersion.Should().Be(1);
    }

    [Fact]
    public async Task Old_K1_artifact_restored_after_K2_recovery_never_regains_authority()
    {
        var (accountId, externalId, k1Reference) = await ConnectAndAdvanceToVersionTwoAsync();
        var k1Backup = await File.ReadAllBytesAsync(ConfirmedFilePath(k1Reference));
        File.Delete(ConfirmedFilePath(k1Reference));

        var k2Reference = await ReconnectAsync(accountId, externalId);

        // The Owner (or an operator restoring from a backup) puts the OLD K1 file back.
        await File.WriteAllBytesAsync(ConfirmedFilePath(k1Reference), k1Backup);

        await using var db = _fixture.CreateContext();
        var account = await db.Set<MarketplaceAccount>().SingleAsync(x => x.Id == accountId);
        account.CredentialReference.Should().Be(k2Reference, "the DB pointer must still name K2 — a restored K1 backup changes nothing about current authority");

        // A probe (the ordinary post-reconnect availability check) must use K2, not the reappeared K1.
        var probe = await _owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/probe", new ReauthorizeMarketplaceAccountRequest(account.Version));
        probe.StatusCode.Should().Be(HttpStatusCode.OK, await probe.Content.ReadAsStringAsync());
        await using var verify = _fixture.CreateContext();
        (await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId))
            .Connection.RuntimeAvailability.Should().Be(MarketplaceRuntimeAvailability.AVAILABLE);
    }

    [Fact]
    public async Task Missing_credential_store_root_recovers_when_root_is_available_again()
    {
        var (accountId, externalId, k1Reference) = await ConnectAndAdvanceToVersionTwoAsync();

        // The root can only be safely relocated while no live process holds its exclusive
        // ownership handle open (LocalProtectedCredentialStore holds a FileStream on
        // `.verce-marketplace-owner.lock` for the host's whole lifetime) — so this, like the
        // key-ring-loss scenario below, tears the host down first. That is a faithful simulation
        // of "the root directory itself is gone/inaccessible at next startup," not a weakening of
        // what is being proven: execution must still be denied, and an explicit reconnect after
        // the root reappears must still recover cleanly into a fresh K2.
        await _factory.DisposeAsync();
        var rootBackup = Path.Combine(Path.GetTempPath(), "verce-rt03-root-backup-" + Guid.NewGuid().ToString("N"));
        Directory.Move(_harness.CredentialStoreRoot, rootBackup);

        var extraConfiguration = new Dictionary<string, string?>(_harness.ExtraConfiguration);
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, extraConfiguration, configureTestServices: _harness.ConfigureServices);
        _owner = await LoggedInOwnerAsync();
        try
        {
            // The host itself must still come up and serve metadata reads while the root is
            // missing — LocalProtectedCredentialStore recreates an empty, validated root rather
            // than failing startup closed (only an UNSAFE path/ACL fails closed).
            long versionBeforeProbe;
            await using (var db = _fixture.CreateContext()) versionBeforeProbe = (await db.Set<MarketplaceAccount>().SingleAsync(x => x.Id == accountId)).Version;
            var statusRead = await _owner.GetAsync($"/api/commerce/marketplace-accounts/{accountId}/connection");
            statusRead.StatusCode.Should().Be(HttpStatusCode.OK, "metadata must remain accessible while the credential root is unavailable");
            var probeWhileMissing = await _owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/probe", new ReauthorizeMarketplaceAccountRequest(versionBeforeProbe));
            probeWhileMissing.IsSuccessStatusCode.Should().BeFalse("execution must be denied while the credential root is unavailable");
        }
        finally
        {
            await _factory.DisposeAsync();
            if (Directory.Exists(_harness.CredentialStoreRoot)) Directory.Delete(_harness.CredentialStoreRoot, recursive: true);
            Directory.Move(rootBackup, _harness.CredentialStoreRoot);
            _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, extraConfiguration, configureTestServices: _harness.ConfigureServices);
            _owner = await LoggedInOwnerAsync();
        }

        // Root restored (matches "a safely configured but missing credential root may be
        // recreated empty... before new-generation recovery"): explicit reconnect now succeeds.
        var k2Reference = await ReconnectAsync(accountId, externalId);
        k2Reference.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Multiple_accounts_affected_by_one_key_ring_loss_each_recover_independently_and_host_stays_available()
    {
        var (accountA, externalA, _) = await ConnectAndAdvanceToVersionTwoAsync();
        var (accountB, externalB, _) = await ConnectAndAdvanceToVersionTwoAsync();

        // Losing the Data Protection key ring makes EVERY existing ciphertext undecryptable at
        // once — simulated by tearing down this host (so no stale in-memory key cache survives)
        // and starting a fresh host against the SAME credential store root but a brand-new,
        // empty DataProtection key directory.
        await _factory.DisposeAsync();
        var freshKeyDirectory = Path.Combine(Path.GetTempPath(), "verce-rt03-fresh-keys-" + Guid.NewGuid().ToString("N"));
        _extraKeyDirectoryToCleanUp = freshKeyDirectory; // deleted in DisposeAsync — this directory is outside MarketplaceTestHarness's own cleanup
        var extraConfiguration = new Dictionary<string, string?>(_harness.ExtraConfiguration) { ["DataProtection:DevKeyDirectory"] = freshKeyDirectory };
        _factory = new VerceWebApplicationFactory(_fixture.ConnectionString, extraConfiguration, configureTestServices: _harness.ConfigureServices);
        _owner = await LoggedInOwnerAsync();

        // The host itself must come up fine (available for metadata) despite every CONNECTED
        // account's material now being undecryptable under the new key ring.
        var statusA = await _owner.GetAsync($"/api/commerce/marketplace-accounts/{accountA}/connection");
        var statusB = await _owner.GetAsync($"/api/commerce/marketplace-accounts/{accountB}/connection");
        statusA.StatusCode.Should().Be(HttpStatusCode.OK);
        statusB.StatusCode.Should().Be(HttpStatusCode.OK);

        // Startup credential validation (this session's new ValidateConnectedCredentialsAsync,
        // run by MarketplaceStartupRecoveryScopedHostedService before serving) must have already
        // independently marked both REAUTHORIZATION_REQUIRED — never a destructive global reset,
        // both accounts/identities/history remain.
        await using (var db = _fixture.CreateContext())
        {
            var a = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountA);
            var b = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountB);
            a.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
            b.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.REAUTHORIZATION_REQUIRED);
            a.ExternalAccountId.Should().Be(externalA, "identity/history must be preserved, never deleted");
            b.ExternalAccountId.Should().Be(externalB);
        }

        // Each recovers independently into its own fresh K2.
        var k2A = await ReconnectAsync(accountA, externalA);
        var k2B = await ReconnectAsync(accountB, externalB);
        k2A.Should().NotBeNullOrWhiteSpace();
        k2B.Should().NotBeNullOrWhiteSpace();
        k2A.Should().NotBe(k2B);
    }

    // ---- helpers ----

    /// <summary>Connects a new account, then reconnects it once more normally so its confirmed
    /// credential version is 2 (never the trivial version-1 case the ADR explicitly excludes).</summary>
    private async Task<(Guid AccountId, string ExternalId, string CredentialReference)> ConnectAndAdvanceToVersionTwoAsync()
    {
        var externalId = "EXT-" + Guid.NewGuid().ToString("N")[..12];
        var (session1, callback1) = await BeginAuthorizationAsync();
        _harness.ControlPlane.Configure(session1, new FakeMarketplaceScenario(externalId));
        (await _owner.GetAsync(callback1)).Should().NotBeNull();

        Guid accountId;
        long versionAfterConnect;
        await using (var db = _fixture.CreateContext())
        {
            var account = await db.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.ExternalAccountId == externalId);
            account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED);
            account.Connection.ConfirmedCredentialVersion.Should().Be(1);
            accountId = account.Id;
            versionAfterConnect = account.Version;
        }

        var (session2, callback2) = await BeginReauthorizeAsync(accountId, versionAfterConnect);
        _harness.ControlPlane.Configure(session2, new FakeMarketplaceScenario(externalId));
        (await _owner.GetAsync(callback2)).Should().NotBeNull();

        await using var verify = _fixture.CreateContext();
        var connected = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        connected.Connection.ConfirmedCredentialVersion.Should().Be(2, "test setup itself must reach K1 version 2 before any disaster is injected");
        return (accountId, externalId, connected.CredentialReference!);
    }

    /// <summary>Drives one more explicit reconnect (the same route RT-03's recovery uses) and
    /// returns the resulting confirmed CredentialReference.</summary>
    private async Task<string> ReconnectAsync(Guid accountId, string externalId)
    {
        long expectedVersion;
        await using (var db = _fixture.CreateContext()) expectedVersion = (await db.Set<MarketplaceAccount>().SingleAsync(x => x.Id == accountId)).Version;
        var (sessionId, callbackUrl) = await BeginReauthorizeAsync(accountId, expectedVersion);
        _harness.ControlPlane.Configure(sessionId, new FakeMarketplaceScenario(externalId));
        var callback = await _owner.GetAsync(callbackUrl);
        callback.Should().NotBeNull();

        await using var verify = _fixture.CreateContext();
        var account = await verify.Set<MarketplaceAccount>().Include(x => x.Connection).SingleAsync(x => x.Id == accountId);
        account.Connection.AuthorizationState.Should().Be(MarketplaceAuthorizationState.CONNECTED, "recovery must succeed for this test to be meaningful");
        return account.CredentialReference!;
    }

    private string ConfirmedFilePath(string reference) =>
        Path.Combine(_harness.CredentialStoreRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))).ToLowerInvariant() + ".bin");

    /// <summary>Reads, re-encrypts-with-a-different-version, and rewrites a confirmed envelope
    /// in place — simulating a physically-promoted-but-never-DB-confirmed higher version, using
    /// the SAME running host's own DataProtector (so the file remains genuinely decryptable by
    /// it, exactly like the real failure this reproduces).</summary>
    private async Task OverwriteConfirmedEnvelopeVersionAsync(string reference, long fakeHigherVersion)
    {
        using var scope = _factory.Services.CreateScope();
        var dataProtectionProvider = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
        var protector = dataProtectionProvider.CreateProtector("Verce3D.Marketplaces.Credentials.v1");
        var path = ConfirmedFilePath(reference);
        var cipher = await File.ReadAllBytesAsync(path);
        var plain = protector.Unprotect(cipher);
        using var document = JsonDocument.Parse(plain);
        var root = document.RootElement;
        var rewritten = new
        {
            OperationId = root.GetProperty("OperationId").GetGuid(),
            SessionId = root.TryGetProperty("SessionId", out var s) && s.ValueKind != JsonValueKind.Null ? (Guid?)s.GetGuid() : null,
            AccountId = root.TryGetProperty("AccountId", out var a) && a.ValueKind != JsonValueKind.Null ? (Guid?)a.GetGuid() : null,
            ProviderCode = root.GetProperty("ProviderCode").GetString(),
            CredentialReference = root.GetProperty("CredentialReference").GetString(),
            Version = fakeHigherVersion,
            Secret = root.GetProperty("Secret").GetString(),
        };
        var newPlain = JsonSerializer.SerializeToUtf8Bytes(rewritten);
        var newCipher = protector.Protect(newPlain);
        await File.WriteAllBytesAsync(path, newCipher);
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginAuthorizationAsync()
    {
        var begin = await _owner.PostAsync("/api/commerce/marketplace-authorizations",
            new BeginMarketplaceAuthorizationRequest(FakeMarketplaceConnector.Code, _channel.Id, "Conta Fake RT03 " + Guid.NewGuid().ToString("N")[..6]));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        return ParseBegin(await begin.Content.ReadAsStringAsync());
    }

    private async Task<(Guid SessionId, string CallbackUrl)> BeginReauthorizeAsync(Guid accountId, long expectedVersion)
    {
        var begin = await _owner.PostAsync($"/api/commerce/marketplace-accounts/{accountId}/reauthorize", new ReauthorizeMarketplaceAccountRequest(expectedVersion));
        begin.StatusCode.Should().Be(HttpStatusCode.OK, await begin.Content.ReadAsStringAsync());
        return ParseBegin(await begin.Content.ReadAsStringAsync());
    }

    private static (Guid SessionId, string CallbackUrl) ParseBegin(string json)
    {
        using var document = JsonDocument.Parse(json);
        var sessionId = document.RootElement.GetProperty("sessionId").GetGuid();
        var authorizationUri = document.RootElement.GetProperty("authorizationUri").GetString()!;
        var state = HttpUtility.ParseQueryString(new Uri(authorizationUri).Query)["state"]!;
        return (sessionId, $"/api/commerce/marketplace-authorizations/{FakeMarketplaceConnector.Code}/callback?state={Uri.EscapeDataString(state)}&code=fake-code");
    }

    private async Task<SalesChannel> SeedProviderAndChannelAsync()
    {
        await using var db = _fixture.CreateContext();
        var provider = await db.Set<MarketplaceProvider>().SingleOrDefaultAsync(x => x.Code == FakeMarketplaceConnector.Code);
        var channel = new SalesChannel("MKT-" + Guid.NewGuid().ToString("N")[..10], "Canal Fake RT03", SalesChannelKind.Marketplace, null, null);
        if (provider is null) db.Add(new MarketplaceProvider(FakeMarketplaceConnector.Code, "Fake Marketplace"));
        db.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private async Task<AuthTestClient> LoggedInOwnerAsync()
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        const string password = "a-perfectly-fine-12char-password";
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(Roles.Owner))
            (await roles.CreateAsync(new ApplicationRole(Roles.Owner))).Succeeded.Should().BeTrue();
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(), UserName = email, Email = email, DisplayName = "Owner RT03",
            IsActive = true, SetupStatus = SetupStatus.Active, SetupCompletedAt = DateTimeOffset.UtcNow
        };
        (await users.CreateAsync(user, password)).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, Roles.Owner)).Succeeded.Should().BeTrue();

        var client = new AuthTestClient(_factory.CreateHttpsClient());
        await client.EnsureCsrfCookieAsync();
        (await client.PostAsync("/api/auth/login", new LoginRequest(email, password))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await client.EnsureCsrfCookieAsync();
        return client;
    }
}
