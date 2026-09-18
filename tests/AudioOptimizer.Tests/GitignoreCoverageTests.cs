namespace AudioOptimizer.Tests;

using System.Diagnostics;
using System.IO;
using Xunit;

/// <summary>
/// Skips — rather than passing — when the guard cannot run. An early <c>return</c> would report green while asserting
/// nothing, which is the vacuity trap this suite guards against everywhere else, and it would be worthless exactly when
/// the guard matters. xUnit 2 has no <c>Assert.Skip</c> (that is v3) and <c>Xunit.SkippableFact</c> would add a
/// dependency, so the skip is set at discovery time.
/// </summary>
public sealed class GitFactAttribute : FactAttribute
{
    public GitFactAttribute()
    {
        if (!Git.IsAvailable) Skip = "git is not on PATH — the repository-layout guard cannot run";
        else if (!Git.HasRepository) Skip = $"no .git under {TestPaths.RepoRoot} — the repository-layout guard cannot run";
    }
}

/// <summary>Minimal git probe and runner, so a missing git skips the guard instead of failing it.</summary>
internal static class Git
{
    public static bool IsAvailable { get; } = Probe("--version") == 0;

    /// <summary>A worktree keeps .git as a file rather than a directory.</summary>
    public static bool HasRepository => Directory.Exists(Path.Combine(TestPaths.RepoRoot, ".git"))
        || File.Exists(Path.Combine(TestPaths.RepoRoot, ".git"));

    /// <summary>Runs git in the repository root and returns stdout; <c>check-ignore</c> writes the ignored paths there.</summary>
    public static string Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = TestPaths.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static int Probe(params string[] arguments)
    {
        try
        {
            Run(arguments);
            return 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }
}

/// <summary>
/// The guard for a defect class no build or test gate can see (boss-1's finding, verified): `.gitignore:329` was the
/// stock `*.dsp`, whose slash-less pattern also matches directories, so it swallowed <c>src/AudioOptimizer.Dsp</c> —
/// the whole DSP library would have been omitted from a commit while the tree looked clean, because "is anything
/// unwanted staged?" answers yes-no-extras while this failure is an omission. The pattern was latent from the initial
/// commit, and inspection cannot find it: only enumerating the intended set and asking git for the committable one can.
/// </summary>
public sealed class GitignoreCoverageTests
{
    private static readonly string[] Roots = ["src", "tests", "tools"];

    [GitFact]
    public void Every_source_file_in_the_tree_is_in_the_committable_set()
    {
        // The reference set comes from the file system, never from git: git is the thing under test. Compared against
        // tracked ∪ addable rather than addable alone, because after a commit the addable set is empty and an
        // addable-only comparison would go red with nothing wrong — the guard would then be deleted as broken.
        var intended = Intended().ToList();
        Assert.NotEmpty(intended);                                     // vacuity guard: a broken walk finds nothing
        HashSet<string> committable = Committable();
        Assert.NotEmpty(committable);

        List<string> missing = [.. intended.Where(path => !committable.Contains(path))];
        Assert.True(missing.Count == 0, $"{missing.Count} file(s) in the tree are excluded from the committable set: {string.Join(", ", missing)}");
        // The specific regression, named rather than only counted.
        Assert.Contains(intended, path => path.StartsWith("src/AudioOptimizer.Dsp/", StringComparison.Ordinal));
        Assert.DoesNotContain(missing, path => path.StartsWith("src/AudioOptimizer.Dsp/", StringComparison.Ordinal));
    }

    [GitFact]
    public void No_source_file_in_the_tree_is_ignored_outside_obj_or_bin()
    {
        string ignored = Git.Run("ls-files", "--others", "--ignored", "--exclude-standard", "--", "*.cs");
        List<string> offenders = [.. Lines(ignored).Where(path => IsInOurRoots(path) && !IsBuildOutput(path))];
        Assert.True(offenders.Count == 0, $"{offenders.Count} ignored source file(s) outside obj/bin: {string.Join(", ", offenders)}");
    }

    [GitFact]
    public void No_direct_child_of_the_source_roots_is_ignored()
    {
        // Direct children only, and that scoping is the point: obj/ and bin/ are supposed to be ignored, so a
        // whole-tree "nothing is ignored" assertion would fail on the fix it is meant to protect. The hazard is a
        // PROJECT directory being swallowed, and a project directory is exactly a direct child of src/, tests/ or tools/.
        List<string> children = [];
        foreach (string root in Roots)
        {
            string absolute = Path.Combine(TestPaths.RepoRoot, root);
            if (!Directory.Exists(absolute)) continue;
            foreach (string directory in Directory.EnumerateDirectories(absolute))
                children.Add($"{root}/{Path.GetFileName(directory)}");
        }

        Assert.NotEmpty(children);                                     // vacuity guard
        string ignored = Git.Run(["check-ignore", "--", .. children]);
        Assert.True(string.IsNullOrWhiteSpace(ignored), $"ignored project directories under the source roots: {ignored}");
    }

    /// <summary>Every file git is expected to carry, from the file system, with build output excluded.</summary>
    private static IEnumerable<string> Intended()
    {
        foreach (string root in Roots)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(TestPaths.RepoRoot, root), "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(TestPaths.RepoRoot, file).Replace('\\', '/');
                if (!IsBuildOutput(relative)) yield return relative;
            }
        }
    }

    /// <summary>Tracked ∪ addable: the set git would carry, independent of whether a commit has happened yet.</summary>
    private static HashSet<string> Committable()
        => [.. Lines(Git.Run(["ls-files", "--cached", "--others", "--exclude-standard", .. Roots]))
            .Where(path => IsInOurRoots(path) && !IsBuildOutput(path))];

    private static bool IsBuildOutput(string path)
        => path.Contains("/obj/", StringComparison.Ordinal) || path.Contains("/bin/", StringComparison.Ordinal);

    private static bool IsInOurRoots(string path)
        => Roots.Any(root => path.StartsWith($"{root}/", StringComparison.Ordinal));

    private static IEnumerable<string> Lines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim().Replace('\\', '/')).Where(line => line.Length > 0);
}
