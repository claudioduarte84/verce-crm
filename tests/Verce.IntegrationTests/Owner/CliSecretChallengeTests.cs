using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Verce.Api.Cli;
using Verce.IntegrationTests.DataProtection;
using Verce.Platform.Cli;
using Verce.Platform.Persistence;

namespace Verce.IntegrationTests.Owner;

/// <summary>
/// D-3, D-4 (ROADMAP S1 catalogue, ADR-0009 §6.2): the bootstrap secret challenge exercised
/// through the REAL <see cref="CliCommands.RunAsync(string[], IServiceProvider, ISecretPrompt)"/>
/// entry point in a Production-like host, with a controlled <see cref="ISecretPrompt"/> standing
/// in for the real no-echo console (mission §42) — never a hard-coded bypass of the actual
/// secret-matching logic.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CliSecretChallengeTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private string _certPath = string.Empty;
    private string _certPasswordPath = string.Empty;
    private string _secretFilePath = string.Empty;
    private const string RealSecret = "correct-horse-battery-staple-0123456789-abcdefghij";
    private const string CertPassword = "test-only-pfx-password";

    public CliSecretChallengeTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE platform.account_setup_token, platform.user_role, platform.user_claim,
                           platform.user_login, platform.user_token, platform.audit_log, platform."user"
            RESTART IDENTITY CASCADE;
            """);

        var cert = EphemeralCertificateFactory.CreateSelfSigned("verce-cli-secret-test");
        _certPath = Path.Combine(Path.GetTempPath(), $"verce-cli-test-{Guid.NewGuid():N}.pfx");
        await File.WriteAllBytesAsync(_certPath, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, CertPassword));
        _certPasswordPath = Path.Combine(Path.GetTempPath(), $"verce-cli-cert-pw-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(_certPasswordPath, CertPassword);

        _secretFilePath = Path.Combine(Path.GetTempPath(), $"verce-cli-secret-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(_secretFilePath, RealSecret);
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_certPath)) File.Delete(_certPath);
        if (File.Exists(_certPasswordPath)) File.Delete(_certPasswordPath);
        if (File.Exists(_secretFilePath)) File.Delete(_secretFilePath);
        Environment.SetEnvironmentVariable("VERCE_BOOTSTRAP_SECRET_FILE", null);
        return Task.CompletedTask;
    }

    private ServiceProvider BuildProductionServices()
    {
        var services = new ServiceCollection();
        var environment = new FakeHostEnvironment("Production");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Verce"] = _fixture.ConnectionString,
                ["DataProtection:CurrentCertificatePath"] = _certPath,
                ["DataProtection:CertificatePasswordFile"] = _certPasswordPath,
            })
            .Build();
        // The real WebApplicationBuilder registers IHostEnvironment into DI automatically;
        // this bare ServiceCollection harness must do it explicitly for CliCommands to resolve it.
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(environment);
        services.AddVercePlatform(configuration, environment, Array.Empty<System.Reflection.Assembly>());
        return services.BuildServiceProvider();
    }

    private sealed class FixedSecretPrompt : ISecretPrompt
    {
        private readonly string _value;
        public FixedSecretPrompt(string value) => _value = value;
        public string ReadCandidate(string promptText) => _value;
    }

    [Fact]
    public async Task D3_a_wrong_candidate_is_rejected_with_exit_code_2_and_a_generic_message()
    {
        Environment.SetEnvironmentVariable("VERCE_BOOTSTRAP_SECRET_FILE", _secretFilePath);
        await using var services = BuildProductionServices();

        var (exitCode, stdErr) = await RunCapturedAsync(
            ["bootstrap-owner", "--email", "d3@example.com"], services, new FixedSecretPrompt("definitely-the-wrong-candidate"));

        exitCode.Should().Be(2);
        stdErr.Should().Contain("bootstrap secret rejected");
    }

    [Fact]
    public async Task D3_a_missing_secret_file_is_rejected_identically_to_a_wrong_candidate()
    {
        Environment.SetEnvironmentVariable("VERCE_BOOTSTRAP_SECRET_FILE", null); // no file configured at all
        await using var services = BuildProductionServices();

        var (exitCode, stdErr) = await RunCapturedAsync(
            ["bootstrap-owner", "--email", "d3b@example.com"], services, new FixedSecretPrompt("anything"));

        exitCode.Should().Be(2);
        stdErr.Should().Contain("bootstrap secret rejected", "a missing expected-secret file must be rejected with the SAME generic message as a wrong candidate");
    }

    [Fact]
    public async Task D3_the_correct_candidate_is_accepted_and_bootstrap_proceeds()
    {
        Environment.SetEnvironmentVariable("VERCE_BOOTSTRAP_SECRET_FILE", _secretFilePath);
        await using var services = BuildProductionServices();

        var (exitCode, _) = await RunCapturedAsync(
            ["bootstrap-owner", "--email", "d3c@example.com"], services, new FixedSecretPrompt(RealSecret));

        exitCode.Should().Be(0, "the correct candidate against the correct expected file must succeed");
    }

    [Fact]
    public async Task D4_the_secret_value_never_appears_in_stdout_or_stderr_on_success_or_failure()
    {
        Environment.SetEnvironmentVariable("VERCE_BOOTSTRAP_SECRET_FILE", _secretFilePath);

        await using (var failingServices = BuildProductionServices())
        {
            var (_, failureOutput) = await RunCapturedAsync(
                ["bootstrap-owner", "--email", "d4-fail@example.com"], failingServices, new FixedSecretPrompt("wrong-candidate-value"));
            failureOutput.Should().NotContain(RealSecret);
            failureOutput.Should().NotContain("wrong-candidate-value", "the rejected candidate must not be echoed either");
        }

        await using var succeedingServices = BuildProductionServices();
        var (_, successOutput) = await RunCapturedAsync(
            ["bootstrap-owner", "--email", "d4-success@example.com"], succeedingServices, new FixedSecretPrompt(RealSecret));
        successOutput.Should().NotContain(RealSecret, "the secret must never be logged even on a successful match");
    }

    [Fact]
    public async Task D13_production_composition_uses_a_console_candidate_and_a_distinct_mounted_file_expected_source()
    {
        Environment.SetEnvironmentVariable("VERCE_BOOTSTRAP_SECRET_FILE", _secretFilePath);
        await using var services = BuildProductionServices();

        var candidate = services.GetRequiredService<ISecretPrompt>();
        var expected = services.GetRequiredService<IExpectedSecretProvider>();

        candidate.Should().BeOfType<ConsoleSecretPrompt>("the default CLI entry point resolves ISecretPrompt from this real production composition");
        expected.Should().BeOfType<MountedFileSecretProvider>("the expected operand is a mounted-file provider, not a console prompt or bare secret environment value");
        expected.Should().NotBeAssignableTo<ISecretPrompt>("both operands must never resolve to one source; that would authenticate anyone able to launch the process");
        expected.LoadExpected("VERCE_BOOTSTRAP_SECRET_FILE").Should().Be(RealSecret);
    }

    private static async Task<(int ExitCode, string CombinedOutput)> RunCapturedAsync(string[] args, IServiceProvider services, ISecretPrompt prompt)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var captured = new StringBuilder();
        var writer = new StringWriter(captured);
        Console.SetOut(writer);
        Console.SetError(writer);
        try
        {
            var exitCode = await CliCommands.RunAsync(args, services, prompt);
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
