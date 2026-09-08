using FluentAssertions;
using Verce.Platform.Cli;

namespace Verce.Platform.Tests.Cli;

/// <summary>
/// ADR-0009 §6.2 — the two-operand secret challenge behind D-3/D-4/D-13. Pure, side-effect-free
/// logic (constant-time comparison, entropy validation, token generation/hashing) kept separate
/// from the console/file I/O in the same class, which belongs to a manual or CLI-driven check
/// rather than an automated test.
/// </summary>
public class SecretChallengeTests
{
    [Fact]
    public void Matches_returns_true_for_two_equal_strings()
    {
        SecretChallenge.Matches("same-secret-value", "same-secret-value").Should().BeTrue();
    }

    [Fact]
    public void Matches_returns_false_for_different_strings_of_the_same_length()
    {
        SecretChallenge.Matches("aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb").Should().BeFalse();
    }

    [Fact]
    public void Matches_returns_false_for_strings_of_different_length()
    {
        // D-13: exercises the length-mismatch branch (hash-then-compare) rather than throwing
        // or crashing on FixedTimeEquals' equal-length requirement.
        SecretChallenge.Matches("short", "a-much-longer-candidate-value").Should().BeFalse();
    }

    [Fact]
    public void Matches_is_never_true_when_comparing_empty_against_non_empty()
    {
        SecretChallenge.Matches(string.Empty, "anything").Should().BeFalse();
    }

    [Fact]
    public void Matches_returns_true_for_two_empty_strings()
    {
        // Documents the boundary; the CALLER (bootstrap flow) is responsible for rejecting an
        // empty expected secret outright via LoadExpectedFromFile's entropy check — Matches
        // itself is a pure comparison and must not silently special-case emptiness.
        SecretChallenge.Matches(string.Empty, string.Empty).Should().BeTrue();
    }

    [Fact]
    public void GenerateSetupToken_raw_and_hash_are_different_and_HashToken_reproduces_the_hash()
    {
        var (raw, hash) = SecretChallenge.GenerateSetupToken();

        raw.Should().NotBeNullOrEmpty();
        hash.Should().NotBeNullOrEmpty();
        raw.Should().NotBe(hash, "the raw token and its hash must never be the same value");
        SecretChallenge.HashToken(raw).Should().Be(hash, "hashing the raw value again must reproduce the same persisted hash");
    }

    [Fact]
    public void GenerateSetupToken_produces_distinct_tokens_across_calls()
    {
        var (raw1, _) = SecretChallenge.GenerateSetupToken();
        var (raw2, _) = SecretChallenge.GenerateSetupToken();

        raw1.Should().NotBe(raw2, "D-5: each issued token must be unique — never reused or predictable");
    }

    [Fact]
    public void HashToken_is_deterministic_for_the_same_input()
    {
        SecretChallenge.HashToken("some-raw-token-value")
            .Should().Be(SecretChallenge.HashToken("some-raw-token-value"));
    }

    [Fact]
    public void HashToken_output_never_contains_the_raw_input_verbatim()
    {
        const string raw = "extremely-recognizable-raw-token-value";
        SecretChallenge.HashToken(raw).Should().NotContain(raw, "D-5: only the hash may ever be persisted or logged");
    }

    [Fact]
    public void LoadExpectedFromFile_throws_when_the_file_does_not_exist()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"verce-secret-does-not-exist-{Guid.NewGuid():N}.txt");

        var act = () => SecretChallenge.LoadExpectedFromFile(missingPath);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public void LoadExpectedFromFile_rejects_a_secret_below_the_minimum_entropy_length()
    {
        var path = Path.Combine(Path.GetTempPath(), $"verce-secret-short-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "too-short");
        try
        {
            var act = () => SecretChallenge.LoadExpectedFromFile(path);
            act.Should().Throw<InvalidOperationException>().WithMessage("*256-bit*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadExpectedFromFile_trims_surrounding_whitespace_and_accepts_a_sufficiently_long_secret()
    {
        var path = Path.Combine(Path.GetTempPath(), $"verce-secret-valid-{Guid.NewGuid():N}.txt");
        var validSecret = new string('a', 43); // exactly the minimum base64url length
        File.WriteAllText(path, $"  {validSecret}\n");
        try
        {
            SecretChallenge.LoadExpectedFromFile(path).Should().Be(validSecret);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
