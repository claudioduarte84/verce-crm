using Microsoft.EntityFrameworkCore;
using Verce.Platform.Cli;
using Verce.Platform.Persistence;

namespace Verce.Api.Cli;

/// <summary>
/// S1 implementation of the administrative CLI verbs documented in OPERATIONS.md /
/// ADR-0009 §6-§9: <c>bootstrap-owner</c>, <c>recover-owner</c>, <c>migrate</c>. Invoked as
/// <c>dotnet Verce.Api.dll &lt;verb&gt; [options]</c> — Program.cs branches on the first
/// argument and exits WITHOUT starting Kestrel, matching the documented invocation exactly.
/// </summary>
public static class CliCommands
{
    private static readonly HashSet<string> KnownVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bootstrap-owner", "recover-owner", "migrate", "recover-data-protection",
    };

    public static bool IsCliInvocation(string[] args) => args.Length > 0 && KnownVerbs.Contains(args[0]);

    public static Task<int> RunAsync(string[] args, IServiceProvider services) =>
        RunAsync(args, services, services.GetRequiredService<ISecretPrompt>());

    /// <summary>Test-observable entry point (D-3/D-4): accepts an <see cref="ISecretPrompt"/> so
    /// a test can supply a controlled candidate value instead of a real console.</summary>
    public static async Task<int> RunAsync(string[] args, IServiceProvider services, ISecretPrompt secretPrompt)
    {
        var verb = args[0].ToLowerInvariant();
        return verb switch
        {
            "migrate" => await RunMigrateAsync(services),
            "bootstrap-owner" => await RunBootstrapOwnerAsync(args, services, secretPrompt),
            "recover-owner" => await RunRecoverOwnerAsync(args, services, secretPrompt),
            "recover-data-protection" => await RunRecoverDataProtectionAsync(args, services, secretPrompt),
            _ => Unknown(verb),
        };
    }

    /// <summary>Shared two-operand secret challenge (ADR-0009 §6.2/§9): expected from a mounted
    /// file, candidate from <paramref name="secretPrompt"/> (a real no-echo console in
    /// production, a controlled value in tests). Skipped in Development so local work and tests
    /// do not require a real secret file. Returns true iff the caller may proceed — the
    /// rejection message is IDENTICAL for a missing file, an unreadable file, and a wrong
    /// candidate (D-3): none of these paths may be distinguishable from outside.</summary>
    private static bool ValidateSecretChallenge(IServiceProvider services, ISecretPrompt secretPrompt, string envVarName, string promptText, string rejectionMessage)
    {
        var environment = services.GetRequiredService<IHostEnvironment>();
        if (environment.IsDevelopment()) return true;

        string expected;
        try { expected = services.GetRequiredService<IExpectedSecretProvider>().LoadExpected(envVarName); }
        catch { Console.Error.WriteLine(rejectionMessage); return false; }

        // The candidate is read via the prompt abstraction — NEVER logged, never echoed, and
        // never taken from a CLI argument (D-4: shell history, `ps`, container logs).
        var candidate = secretPrompt.ReadCandidate(promptText);
        if (!SecretChallenge.Matches(expected, candidate))
        {
            Console.Error.WriteLine(rejectionMessage);
            return false;
        }

        return true;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"Unknown command '{verb}'.");
        return 1;
    }

    private static async Task<int> RunMigrateAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<VerceDbContext>();
        await context.Database.MigrateAsync();
        Console.WriteLine("Migrations applied.");
        return 0;
    }

    private static async Task<int> RunBootstrapOwnerAsync(string[] args, IServiceProvider services, ISecretPrompt secretPrompt)
    {
        var email = GetOption(args, "--email");
        if (email is null)
        {
            Console.Error.WriteLine("Usage: bootstrap-owner --email <email> [--name <display name>]");
            return 1;
        }
        var displayName = GetOption(args, "--name") ?? email;

        if (!ValidateSecretChallenge(services, secretPrompt, "VERCE_BOOTSTRAP_SECRET_FILE", "Enter bootstrap secret: ", "bootstrap secret rejected"))
            return 2;

        using var scope = services.CreateScope();
        var bootstrapService = scope.ServiceProvider.GetRequiredService<OwnerBootstrapService>();
        var (outcome, rawToken, userId) = await bootstrapService.BootstrapOwnerAsync(email, displayName);

        if (outcome == Verce.Platform.Cli.BootstrapOutcome.OwnerAlreadyExists)
        {
            Console.Error.WriteLine("an Owner already exists");
            return 3;
        }

        Console.WriteLine($"Owner created: {email} (id: {userId})");
        Console.WriteLine("Setup URL (valid for 30 minutes, single use):");
        Console.WriteLine($"  https://<host>/setup-account?token={rawToken}");
        return 0;
    }

    private static async Task<int> RunRecoverOwnerAsync(string[] args, IServiceProvider services, ISecretPrompt secretPrompt)
    {
        var email = GetOption(args, "--email");
        if (email is null)
        {
            Console.Error.WriteLine("Usage: recover-owner --email <email>");
            return 1;
        }

        if (!ValidateSecretChallenge(services, secretPrompt, "VERCE_RECOVERY_SECRET_FILE", "Enter recovery secret: ", "recovery secret rejected"))
            return 2;

        using var scope = services.CreateScope();
        var bootstrapService = scope.ServiceProvider.GetRequiredService<OwnerBootstrapService>();
        var (outcome, rawToken) = await bootstrapService.RecoverOwnerAsync(email);

        if (outcome == Verce.Platform.Cli.RecoveryOutcome.UserNotFound)
        {
            Console.Error.WriteLine($"no user found for {email}");
            return 3;
        }

        Console.WriteLine("Recovery token issued (valid for 30 minutes, single use):");
        Console.WriteLine($"  https://<host>/setup-account?token={rawToken}");
        return 0;
    }

    /// <summary>ADR-0008 §2.4 / OPERATIONS §5.2: offline, deliberately destructive Data
    /// Protection crypto recovery. Never reachable over HTTP — see
    /// <see cref="Verce.Platform.DataProtection.DataProtectionRecoveryService"/> for the full
    /// procedure and the documented ai_settings gap.</summary>
    private static async Task<int> RunRecoverDataProtectionAsync(string[] args, IServiceProvider services, ISecretPrompt secretPrompt)
    {
        if (!args.Contains("--confirm-destroy-secrets", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "Refusing: this command destructively archives unreadable Data Protection keys. " +
                "Pass --confirm-destroy-secrets to proceed.");
            return 1;
        }

        if (!ValidateSecretChallenge(services, secretPrompt, "VERCE_RECOVERY_SECRET_FILE", "Enter recovery secret: ", "recovery secret rejected"))
            return 2;

        using var scope = services.CreateScope();
        var recoveryService = scope.ServiceProvider.GetRequiredService<Verce.Platform.DataProtection.DataProtectionRecoveryService>();
        var (outcome, archivedCount) = await recoveryService.RecoverAsync(operatorUserId: null);

        if (outcome == Verce.Platform.DataProtection.DataProtectionRecoveryOutcome.RefusedRingStillDecryptable)
        {
            Console.Error.WriteLine(
                "Refusing: the current certificate ring can still decrypt every existing key. " +
                "This command exists for genuine disaster recovery only.");
            return 3;
        }

        Console.WriteLine($"Recovery complete. {archivedCount} unreadable key(s) archived to platform.data_protection_key_archive.");
        Console.WriteLine("A new key has been created with the current certificate. All sessions have been invalidated.");
        Console.WriteLine("The application can now start normally. An Owner must log in and re-enter any external API keys.");
        return 0;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
