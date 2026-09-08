namespace Verce.Architecture.Tests;

/// <summary>Locates the repository root (where Verce.slnx lives) from the test binary's
/// location, so source-level architecture rules can scan .cs files directly.</summary>
public static class RepoPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string SrcDirectory => Path.Combine(RepoRoot, "src");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("Verce.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root (Verce.slnx) from " + AppContext.BaseDirectory);
    }

    public static IEnumerable<string> AllSourceFiles(string subdirectory = "src") =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, subdirectory), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));
}
