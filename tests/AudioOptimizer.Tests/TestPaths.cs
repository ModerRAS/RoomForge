namespace AudioOptimizer.Tests;

/// <summary>
/// Locates the repository root from the test binaries. Used by the artifact-hygiene assertions (no rendered
/// PNG may land in the tree) and by the source-scan checks that prove a code path is unreachable.
/// </summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SourceRoot => Path.Combine(RepoRoot, "src");

    public static IEnumerable<string> ProductionSources() => Directory.GetFiles(SourceRoot, "*.cs", SearchOption.AllDirectories);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AudioOptimizer.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
