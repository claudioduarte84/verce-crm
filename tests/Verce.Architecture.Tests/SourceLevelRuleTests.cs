using System.Text.RegularExpressions;
using FluentAssertions;

namespace Verce.Architecture.Tests;

/// <summary>
/// Rules that are about call SITES, not type members, and so cannot be checked by reflecting
/// over compiled assemblies — a source-text scan is the standard, legitimate technique
/// (CLAUDE.md rules; ADR-0011 §1.2, §4).
/// </summary>
public class SourceLevelRuleTests
{
    private static readonly string[] AllowedClockFiles =
    {
        "SystemClock.cs", "TestClock.cs", "IClock.cs",
    };

    [Fact]
    public void No_DateTime_Now_or_UtcNow_outside_IClock_implementations()
    {
        var violations = new List<string>();
        var pattern = new Regex(@"DateTime\.(Now|UtcNow)\b", RegexOptions.Compiled);

        foreach (var file in RepoPaths.AllSourceFiles())
        {
            var fileName = Path.GetFileName(file);
            if (AllowedClockFiles.Contains(fileName)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                    violations.Add($"{Path.GetRelativePath(RepoPaths.RepoRoot, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        violations.Should().BeEmpty(
            "DateTime.Now/UtcNow must only appear inside IClock implementations — everything else must inject IClock");
    }

    [Fact]
    public void No_business_ordering_by_Id_anywhere()
    {
        // ADR-0011 §1.2: UUID v7 is identity/locality, never business ordering.
        var violations = new List<string>();
        var linqPattern = new Regex(@"OrderBy(Descending)?\s*\(\s*\w+\s*=>\s*\w+\.Id\b", RegexOptions.Compiled);
        var sqlPattern = new Regex(@"ORDER\s+BY\s+(\w+\.)?id\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        foreach (var file in RepoPaths.AllSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (linqPattern.IsMatch(lines[i]) || sqlPattern.IsMatch(lines[i]))
                    violations.Add($"{Path.GetRelativePath(RepoPaths.RepoRoot, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        violations.Should().BeEmpty("business ordering must use created_at, a business date, an explicit sequence, or sort_order — never Id");
    }

    [Fact]
    public void No_float_or_double_declared_in_domain_or_platform_source()
    {
        // ADR-0002: money/percent/quantity must never use float/double.
        var violations = new List<string>();
        // Matches a field/property/local/parameter TYPE declaration of float or double —
        // deliberately conservative (word-boundary on both sides) to avoid matching identifiers
        // that merely contain "double"/"float" as a substring.
        var pattern = new Regex(@"(?<![\w.])(float|double)(?![\w])", RegexOptions.Compiled);

        var domainDirs = new[] { "Verce.SharedKernel", "Verce.Platform" }
            .Concat(Directory.EnumerateDirectories(Path.Combine(RepoPaths.SrcDirectory, "Modules"))
                .Select(d => Path.GetFileName(d)!));

        foreach (var file in RepoPaths.AllSourceFiles())
        {
            var relative = Path.GetRelativePath(RepoPaths.SrcDirectory, file);
            var topDir = relative.Split(Path.DirectorySeparatorChar)[0];
            if (!domainDirs.Contains(topDir) && !relative.StartsWith("Modules")) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///")) continue;
                if (pattern.IsMatch(line))
                    violations.Add($"{Path.GetRelativePath(RepoPaths.RepoRoot, file)}:{i + 1}: {line.Trim()}");
            }
        }

        violations.Should().BeEmpty("float/double are banned in domain assemblies (ADR-0002) — use decimal via Money/Percent/Grams/Kwh/Quantity");
    }
}
