using System.Security.Cryptography;
using System.Text;

namespace Verce.Platform.Cli;

/// <summary>
/// The CANDIDATE half of the secret challenge, factored out so CLI commands can be tested with
/// a controlled value instead of a real console (mission §42) — production wires
/// <see cref="ConsoleSecretPrompt"/>; tests supply their own <see cref="ISecretPrompt"/>.
/// </summary>
public interface ISecretPrompt
{
    string ReadCandidate(string promptText);
}

/// <summary>The expected half of the challenge. It is intentionally a distinct service from
/// <see cref="ISecretPrompt"/> so production composition cannot equate a mounted secret file
/// with the operator's interactive candidate.</summary>
public interface IExpectedSecretProvider
{
    string LoadExpected(string environmentVariableName);
}

public sealed class MountedFileSecretProvider : IExpectedSecretProvider
{
    public string LoadExpected(string environmentVariableName)
    {
        var path = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Expected secret file is not configured.");
        return SecretChallenge.LoadExpectedFromFile(path);
    }
}

/// <summary>Production implementation: a silent, no-echo console prompt. Never a CLI argument.</summary>
public sealed class ConsoleSecretPrompt : ISecretPrompt
{
    public string ReadCandidate(string promptText) => SecretChallenge.ReadCandidateFromConsole(promptText);
}

/// <summary>
/// Two-operand secret challenge (ADR-0009 §6.2). The EXPECTED value and the CANDIDATE value
/// must come from genuinely different sources — comparing a mounted file against itself is not
/// authentication, it succeeds for anyone who can run the process.
/// </summary>
public static class SecretChallenge
{
    public const int MinimumEntropyBits = 256;
    private const int MinimumBase64UrlLength = 43; // 256 bits of entropy, base64url-encoded

    /// <summary>Reads the EXPECTED secret from a mounted file. Validates minimum entropy at
    /// load time, not at use — a short/weak file fails immediately and loudly.</summary>
    public static string LoadExpectedFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new InvalidOperationException($"Secret file not found: '{filePath}'.");

        var content = File.ReadAllText(filePath).Trim();
        if (content.Length < MinimumBase64UrlLength)
            throw new InvalidOperationException(
                $"Secret at '{filePath}' is shorter than the minimum {MinimumEntropyBits}-bit entropy " +
                $"requirement ({MinimumBase64UrlLength} base64url characters).");

        return content;
    }

    /// <summary>Reads the CANDIDATE secret from a silent, no-echo console prompt. Never a CLI
    /// argument (shell history, `ps`, container logs) and never the same source as the
    /// expected value.</summary>
    public static string ReadCandidateFromConsole(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    /// <summary>Constant-time comparison — never <c>==</c>, never <c>string.Equals</c>. The
    /// outcome (accepted/rejected) is logged; neither operand ever is.</summary>
    public static bool Matches(string expected, string candidate)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        // FixedTimeEquals requires equal-length spans; unequal lengths are hashed first so
        // the comparison itself never leaks a length-based timing signal either.
        if (expectedBytes.Length != candidateBytes.Length)
        {
            expectedBytes = SHA256.HashData(expectedBytes);
            candidateBytes = SHA256.HashData(candidateBytes);
        }
        return CryptographicOperations.FixedTimeEquals(expectedBytes, candidateBytes);
    }

    /// <summary>Generates a fresh 256-bit single-use token, returning both the raw value (shown
    /// once, never stored) and its SHA-256 hash (the only thing persisted).</summary>
    public static (string RawToken, string Sha256Hash) GenerateSetupToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32); // 256 bits
        var raw = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return (raw, hash);
    }

    public static string HashToken(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
