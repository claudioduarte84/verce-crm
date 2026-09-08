using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Verce.Platform.Cli;
using Verce.Platform.DataProtection;
using Verce.Platform.Identity;
using Verce.Platform.Persistence;
using Verce.SharedKernel.Time;

namespace Verce.IntegrationTests.DataProtection;

/// <summary>
/// E-1..E-9 (ROADMAP S1 catalogue, ADR-0008 §2) against the REAL ASP.NET Core Data Protection
/// stack — real certificates, real PostgreSQL-backed key persistence, real encryption/decryption
/// — never a boolean mock restating configuration (mission §9).
///
/// E-6's "ai_settings" sub-clause is not testable here: <c>ai_settings</c> is S13 (AI Insights)
/// scope and does not exist in S1's schema (see DataProtectionRecoveryService's doc comment and
/// the S1 FINAL REPORT's OPEN DECISIONS). Every other effect of E-6 IS tested.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DataProtectionRecoveryTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    public DataProtectionRecoveryTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE platform.data_protection_key_archive,
                           platform.data_protection_keys,
                           platform.user_role, platform.user_claim, platform.user_login,
                           platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task E1_restoring_with_the_same_certificate_ring_decrypts_the_existing_secret()
    {
        var cert = EphemeralCertificateFactory.CreateSelfSigned("verce-e1");
        const string secret = "sk-super-secret-openai-key";

        string ciphertext;
        await using (var first = DataProtectionTestHost.Build(_fixture.ConnectionString, cert))
        {
            var protector = DataProtectionTestHost.Provider(first).CreateProtector("Verce3D.Tests.E1");
            ciphertext = protector.Protect(secret);
        }

        // Simulates a restart: a brand-new provider instance, same cert, same database.
        await using var second = DataProtectionTestHost.Build(_fixture.ConnectionString, cert);
        var reloadedProtector = DataProtectionTestHost.Provider(second).CreateProtector("Verce3D.Tests.E1");
        reloadedProtector.Unprotect(ciphertext).Should().Be(secret);
    }

    [Fact]
    public void E2_production_missing_a_certificate_prevents_the_service_registration_from_completing()
    {
        var services = new ServiceCollection();
        var environment = new FakeHostEnvironment("Production");
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Verce"] = _fixture.ConnectionString,
                // Deliberately NO DataProtection:CurrentCertificatePath.
            })
            .Build();

        var act = () => services.AddVercePlatform(configuration, environment, Array.Empty<System.Reflection.Assembly>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*recover-data-protection*", "the error must name the recovery command, not just fail silently");
    }

    [Fact]
    public async Task E2a_the_host_never_binds_http_when_startup_validation_fails_so_probes_get_a_connection_failure_not_a_503()
    {
        // The same failure as E-2, but proven through the REAL Program.cs composition root via
        // WebApplicationFactory: if service registration throws, the host never starts, so no
        // requirement anywhere may expect a 503 — there is no listening socket to answer at all.
        await using var factory = new Auth.VerceWebApplicationFactory(_fixture.ConnectionString);
        var configuredFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // No DataProtection:CurrentCertificatePath — Production must fail closed.
            }));
        });

        var act = () => configuredFactory.CreateClient();
        act.Should().Throw<InvalidOperationException>("startup validation must fail before Kestrel ever binds — there is no partially-started state to probe");
    }

    [Fact]
    public async Task E2b_offline_recovery_completes_without_the_web_host_running_and_the_host_starts_normally_afterward()
    {
        var brokenCert = EphemeralCertificateFactory.CreateSelfSigned("verce-e2b-broken");
        var recoveryCert = EphemeralCertificateFactory.CreateSelfSigned("verce-e2b-recovery");

        // Protect something with a cert that will then become totally unavailable.
        await using (var initial = DataProtectionTestHost.Build(_fixture.ConnectionString, brokenCert))
        {
            DataProtectionTestHost.Provider(initial).CreateProtector("Verce3D.Tests.E2b").Protect("anything");
        }

        // Recovery runs "offline" — a plain service provider, no web host, no Kestrel — using a
        // ring that can no longer decrypt (brokenCert is gone; only recoveryCert is configured).
        await using var recoveryServices = DataProtectionTestHost.Build(_fixture.ConnectionString, recoveryCert);
        var context = recoveryServices.GetRequiredService<VerceDbContext>();
        var keyManager = DataProtectionTestHost.KeyManager(recoveryServices);
        var recoveryService = new DataProtectionRecoveryService(context, keyManager, new SystemClock());

        var (outcome, archivedCount) = await recoveryService.RecoverAsync(operatorUserId: null);
        outcome.Should().Be(DataProtectionRecoveryOutcome.Recovered);
        archivedCount.Should().Be(1);

        // "The host starts normally afterwards": a fresh provider with the SAME recovery cert
        // can now protect/unprotect new values without error.
        await using var afterRecovery = DataProtectionTestHost.Build(_fixture.ConnectionString, recoveryCert);
        var protector = DataProtectionTestHost.Provider(afterRecovery).CreateProtector("Verce3D.Tests.E2b.after");
        protector.Unprotect(protector.Protect("new value")).Should().Be("new value");
    }

    [Fact]
    public async Task E3_rotation_keeps_the_old_payload_readable_through_the_unprotect_ring()
    {
        var cert1 = EphemeralCertificateFactory.CreateSelfSigned("verce-e3-cert1");
        var cert2 = EphemeralCertificateFactory.CreateSelfSigned("verce-e3-cert2");

        string ciphertext;
        await using (var whileCert1Current = DataProtectionTestHost.Build(_fixture.ConnectionString, cert1))
        {
            ciphertext = DataProtectionTestHost.Provider(whileCert1Current).CreateProtector("Verce3D.Tests.E3").Protect("value-under-cert1");
        }

        // cert2 becomes current; the ring is [cert2, cert1] — cert1 stays for unprotecting history.
        await using var afterRotation = DataProtectionTestHost.Build(_fixture.ConnectionString, cert2, cert1);
        var protector = DataProtectionTestHost.Provider(afterRotation).CreateProtector("Verce3D.Tests.E3");
        protector.Unprotect(ciphertext).Should().Be("value-under-cert1");
    }

    [Fact]
    public async Task E4_creating_a_new_key_never_rewraps_any_pre_existing_key()
    {
        var cert1 = EphemeralCertificateFactory.CreateSelfSigned("verce-e4-cert1");
        var cert2 = EphemeralCertificateFactory.CreateSelfSigned("verce-e4-cert2");

        await using (var whileCert1Current = DataProtectionTestHost.Build(_fixture.ConnectionString, cert1))
        {
            // Forces at least one real key to exist, wrapped with cert1.
            DataProtectionTestHost.Provider(whileCert1Current).CreateProtector("Verce3D.Tests.E4").Protect("value");
        }

        await using var context = _fixture.CreateContext();
        var beforeRows = await context.DataProtectionKeys.AsNoTracking().ToDictionaryAsync(k => k.Id, k => k.Xml);
        beforeRows.Should().NotBeEmpty();

        await using var afterRotation = DataProtectionTestHost.Build(_fixture.ConnectionString, cert2, cert1);
        DataProtectionTestHost.KeyManager(afterRotation).CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

        await using var verify = _fixture.CreateContext();
        var afterRows = await verify.DataProtectionKeys.AsNoTracking().ToDictionaryAsync(k => k.Id, k => k.Xml);

        afterRows.Should().HaveCountGreaterThan(beforeRows.Count, "CreateNewKey must add a new row");
        foreach (var (id, xml) in beforeRows)
            afterRows[id].Should().Be(xml, "every pre-existing key's XML must be byte-identical — nothing may re-wrap it");
    }

    [Fact]
    public async Task E4a_the_old_certificate_stays_effective_in_the_unprotect_ring_after_rotation()
    {
        // The same observable proof as E-3, restated as "the predecessor certificate was never
        // automatically dropped" — the ring's caller-supplied list is exactly what makes this
        // true, and this test is the regression net for someone "cleaning up" the ring later.
        var cert1 = EphemeralCertificateFactory.CreateSelfSigned("verce-e4a-cert1");
        var cert2 = EphemeralCertificateFactory.CreateSelfSigned("verce-e4a-cert2");

        string ciphertext;
        await using (var whileCert1Current = DataProtectionTestHost.Build(_fixture.ConnectionString, cert1))
        {
            ciphertext = DataProtectionTestHost.Provider(whileCert1Current).CreateProtector("Verce3D.Tests.E4a").Protect("still-here");
        }

        await using var afterRotation = DataProtectionTestHost.Build(_fixture.ConnectionString, cert2, cert1);
        DataProtectionTestHost.Provider(afterRotation).CreateProtector("Verce3D.Tests.E4a").Unprotect(ciphertext).Should().Be("still-here");
    }

    [Fact]
    public async Task E5_explicit_recovery_restores_operability_after_an_unreadable_ring()
    {
        var lostCert = EphemeralCertificateFactory.CreateSelfSigned("verce-e5-lost");
        var recoveryCert = EphemeralCertificateFactory.CreateSelfSigned("verce-e5-recovery");

        await using (var initial = DataProtectionTestHost.Build(_fixture.ConnectionString, lostCert))
        {
            DataProtectionTestHost.Provider(initial).CreateProtector("Verce3D.Tests.E5").Protect("value");
        }

        await using var recoveryServices = DataProtectionTestHost.Build(_fixture.ConnectionString, recoveryCert);
        var recoveryService = new DataProtectionRecoveryService(
            recoveryServices.GetRequiredService<VerceDbContext>(), DataProtectionTestHost.KeyManager(recoveryServices), new SystemClock());

        var (outcome, _) = await recoveryService.RecoverAsync(operatorUserId: null);
        outcome.Should().Be(DataProtectionRecoveryOutcome.Recovered);

        await using var afterRecovery = DataProtectionTestHost.Build(_fixture.ConnectionString, recoveryCert);
        var protector = DataProtectionTestHost.Provider(afterRecovery).CreateProtector("Verce3D.Tests.E5.after");
        protector.Unprotect(protector.Protect("app is usable again")).Should().Be("app is usable again");
    }

    [Fact]
    public async Task E6_recovery_invalidates_every_session_but_leaves_passwords_untouched()
    {
        var lostCert = EphemeralCertificateFactory.CreateSelfSigned("verce-e6-lost");
        var recoveryCert = EphemeralCertificateFactory.CreateSelfSigned("verce-e6-recovery");

        await using (var initial = DataProtectionTestHost.Build(_fixture.ConnectionString, lostCert))
        {
            DataProtectionTestHost.Provider(initial).CreateProtector("Verce3D.Tests.E6").Protect("value");
        }

        Guid userId;
        string passwordHashBefore;
        string securityStampBefore;
        await using (var context = _fixture.CreateContext())
        {
            var userStore = new UserStore<ApplicationUser, ApplicationRole, VerceDbContext, Guid>(context);
            var userManager = new UserManager<ApplicationUser>(
                userStore, Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                new PasswordHasher<ApplicationUser>(), Array.Empty<IUserValidator<ApplicationUser>>(),
                Array.Empty<IPasswordValidator<ApplicationUser>>(), new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(), null!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<ApplicationUser>>());

            var user = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                UserName = "e6@example.com",
                Email = "e6@example.com",
                DisplayName = "E6 Test",
                IsActive = true,
                SetupStatus = SetupStatus.Active,
                SetupCompletedAt = DateTimeOffset.UtcNow,
            };
            await userManager.CreateAsync(user, "a-perfectly-fine-12char-password");
            userId = user.Id;
            passwordHashBefore = user.PasswordHash!;
            securityStampBefore = user.SecurityStamp!;
        }

        await using var recoveryServices = DataProtectionTestHost.Build(_fixture.ConnectionString, recoveryCert);
        var recoveryService = new DataProtectionRecoveryService(
            recoveryServices.GetRequiredService<VerceDbContext>(), DataProtectionTestHost.KeyManager(recoveryServices), new SystemClock());
        await recoveryService.RecoverAsync(operatorUserId: null);

        await using var verify = _fixture.CreateContext();
        var reloaded = await verify.Users.SingleAsync(u => u.Id == userId);
        reloaded.SecurityStamp.Should().NotBe(securityStampBefore, "E-6: every session must be invalidated");
        reloaded.PasswordHash.Should().Be(passwordHashBefore, "E-6: passwords are Identity hashes, not Data Protection payloads — unaffected");

        // The ai_settings.api_key_* / is_enabled / api_key_recovery_required sub-clause of E-6
        // is not testable in S1 — see the class-level doc comment.
    }

    [Fact]
    public async Task E7_recovery_archives_the_unreadable_row_rather_than_deleting_it()
    {
        var lostCert = EphemeralCertificateFactory.CreateSelfSigned("verce-e7-lost");
        var recoveryCert = EphemeralCertificateFactory.CreateSelfSigned("verce-e7-recovery");

        int originalKeyId;
        string originalXml;
        await using (var initial = DataProtectionTestHost.Build(_fixture.ConnectionString, lostCert))
        {
            DataProtectionTestHost.Provider(initial).CreateProtector("Verce3D.Tests.E7").Protect("value");
        }
        await using (var context = _fixture.CreateContext())
        {
            var row = await context.DataProtectionKeys.SingleAsync();
            originalKeyId = row.Id;
            originalXml = row.Xml!;
        }

        await using var recoveryServices = DataProtectionTestHost.Build(_fixture.ConnectionString, recoveryCert);
        var recoveryService = new DataProtectionRecoveryService(
            recoveryServices.GetRequiredService<VerceDbContext>(), DataProtectionTestHost.KeyManager(recoveryServices), new SystemClock());
        await recoveryService.RecoverAsync(operatorUserId: null);

        await using var verify = _fixture.CreateContext();
        (await verify.DataProtectionKeys.AnyAsync(k => k.Id == originalKeyId)).Should().BeFalse("the unreadable row must be removed from the live table");

        var archived = await verify.DataProtectionKeyArchive.SingleAsync(a => a.OriginalKeyId == originalKeyId);
        archived.Xml.Should().Be(originalXml, "the archived copy must be byte-identical — never destroyed, only moved");
    }

    [Fact]
    public async Task E8_recovery_refuses_when_the_ring_can_still_decrypt_every_key()
    {
        var cert = EphemeralCertificateFactory.CreateSelfSigned("verce-e8");

        await using (var initial = DataProtectionTestHost.Build(_fixture.ConnectionString, cert))
        {
            DataProtectionTestHost.Provider(initial).CreateProtector("Verce3D.Tests.E8").Protect("value");
        }

        // The SAME certificate is still in the ring — nothing is actually broken.
        await using var stillWorking = DataProtectionTestHost.Build(_fixture.ConnectionString, cert);
        var recoveryService = new DataProtectionRecoveryService(
            stillWorking.GetRequiredService<VerceDbContext>(), DataProtectionTestHost.KeyManager(stillWorking), new SystemClock());

        var (outcome, archivedCount) = await recoveryService.RecoverAsync(operatorUserId: null);
        outcome.Should().Be(DataProtectionRecoveryOutcome.RefusedRingStillDecryptable, "this command must not be usable casually");
        archivedCount.Should().Be(0);

        await using var verify = _fixture.CreateContext();
        (await verify.DataProtectionKeyArchive.CountAsync()).Should().Be(0, "nothing may be archived when the ring was never actually broken");
    }

    [Fact]
    public async Task E9_the_development_profile_starts_with_no_certificate_configured()
    {
        var devKeyDirectory = Path.Combine(Path.GetTempPath(), $"verce-e9-devkeys-{Guid.NewGuid():N}");
        try
        {
            var services = new ServiceCollection();
            var environment = new FakeHostEnvironment("Development");
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Verce"] = _fixture.ConnectionString,
                    ["DataProtection:DevKeyDirectory"] = devKeyDirectory,
                    // No DataProtection:CurrentCertificatePath — Development must not require one.
                })
                .Build();

            var act = () => services.AddVercePlatform(configuration, environment, Array.Empty<System.Reflection.Assembly>());
            act.Should().NotThrow("Development must remain usable without a certificate (E-9)");

            var provider = services.BuildServiceProvider();
            var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Verce3D.Tests.E9");
            var ciphertext = protector.Protect("dev-value");
            protector.Unprotect(ciphertext).Should().Be("dev-value");

            Directory.Exists(devKeyDirectory).Should().BeTrue("the file-system key ring must actually be created where configured");
        }
        finally
        {
            if (Directory.Exists(devKeyDirectory)) Directory.Delete(devKeyDirectory, recursive: true);
        }
    }
}
