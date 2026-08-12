using System.Diagnostics;
using DumpDebugger.Core.Findings;
using DumpDebugger.Core.Source;
using DumpDebugger.Llm;

namespace DumpDebugger.Tests;

public sealed class SourceSnippetProviderTests : IDisposable
{
    private readonly string _repoDir = Directory.CreateTempSubdirectory("dumpdebugger-snippet-test-").FullName;
    private readonly string _commitSha;

    public SourceSnippetProviderTests()
    {
        RunGit(_repoDir, "init -q");
        RunGit(_repoDir, "config user.email test@example.com");
        RunGit(_repoDir, "config user.name Test");

        var lines = Enumerable.Range(1, 30).Select(i => $"line {i}");
        File.WriteAllText(Path.Combine(_repoDir, "Order.cs"), string.Join('\n', lines) + "\n");
        RunGit(_repoDir, "add Order.cs");
        RunGit(_repoDir, "commit -q -m initial");

        _commitSha = RunGit(_repoDir, "rev-parse HEAD").Trim();
    }

    [Fact]
    public async Task BuildAsync_ReadsWindowAroundResolvedLocation()
    {
        var location = new SourceLocation("https://github.com/org/repo", _commitSha, "Order.cs", 15, 1);
        var findings = MakeFindingsDocument(location);
        RepoContext[] repos = [new RepoContext(_repoDir, "https://github.com/org/repo")];

        var snippets = await SourceSnippetProvider.BuildAsync(findings, repos, CancellationToken.None);

        var snippet = Assert.Single(snippets);
        Assert.Equal("Order.cs", snippet.RepoRelativePath);
        Assert.Equal(15, snippet.Line);
        Assert.Contains("line 15", snippet.Code);
        Assert.Contains("line 9", snippet.Code); // within the +/-6 line window
        Assert.DoesNotContain("line 1\n", snippet.Code); // outside the window
        Assert.NotNull(snippet.Blame);
        Assert.Equal("Test", snippet.Blame!.Author);
    }

    [Fact]
    public async Task BuildAsync_PicksTheRepoWhoseUrlMatches_OutOfSeveralAssociated()
    {
        var location = new SourceLocation("https://github.com/org/repo", _commitSha, "Order.cs", 15, 1);
        var findings = MakeFindingsDocument(location);

        // A second, unrelated repo is associated too (e.g. an internal NuGet package's repo) —
        // its path doesn't even exist, so picking the wrong one would fail loudly, not quietly.
        RepoContext[] repos =
        [
            new RepoContext("/nonexistent/other-repo", "https://github.com/org/other-repo"),
            new RepoContext(_repoDir, "https://github.com/org/repo"),
        ];

        var snippets = await SourceSnippetProvider.BuildAsync(findings, repos, CancellationToken.None);

        var snippet = Assert.Single(snippets);
        Assert.Equal("Order.cs", snippet.RepoRelativePath);
    }

    [Fact]
    public async Task BuildAsync_SkipsLocation_WhenNoAssociatedRepoMatchesAmongSeveral()
    {
        var location = new SourceLocation("https://github.com/org/repo", _commitSha, "Order.cs", 15, 1);
        var findings = MakeFindingsDocument(location);

        RepoContext[] repos =
        [
            new RepoContext("/nonexistent/a", "https://github.com/org/a"),
            new RepoContext("/nonexistent/b", "https://github.com/org/b"),
        ];

        var snippets = await SourceSnippetProvider.BuildAsync(findings, repos, CancellationToken.None);

        Assert.Empty(snippets);
    }

    [Fact]
    public async Task BuildAsync_SkipsLocationsWithNoResolvableEvidence()
    {
        var findings = new FindingsDocument(
            FindingsDocument.CurrentSchemaVersion,
            new FindingsDumpSummary("dump.dmp", "net8.0", "x64", null, false),
            [new Finding("f1", "TestAnalyzer", Severity.Info, Confidence.High, "Title", "Summary", [], [])]);
        RepoContext[] repos = [new RepoContext(_repoDir, "https://github.com/org/repo")];

        var snippets = await SourceSnippetProvider.BuildAsync(findings, repos, CancellationToken.None);

        Assert.Empty(snippets);
    }

    [Fact]
    public void FormatForPrompt_ReturnsEmptyString_WhenNoSnippets()
    {
        Assert.Equal("", SourceSnippetProvider.FormatForPrompt([]));
    }

    [Fact]
    public void FormatForPrompt_IncludesPathAndLineHeader()
    {
        var snippet = new SourceSnippet("Order.cs", _commitSha, 15, "  15: line 15\n");

        var formatted = SourceSnippetProvider.FormatForPrompt([snippet]);

        Assert.Contains("Order.cs", formatted);
        Assert.Contains("line 15", formatted);
        Assert.Contains(_commitSha[..8], formatted);
    }

    [Fact]
    public void FormatForPrompt_IncludesBlameLine_WhenPresent()
    {
        var blame = new BlameInfo(_commitSha, "Jane Doe", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), "fix the thing");
        var snippet = new SourceSnippet("Order.cs", _commitSha, 15, "  15: line 15\n", blame);

        var formatted = SourceSnippetProvider.FormatForPrompt([snippet]);

        Assert.Contains("Jane Doe", formatted);
        Assert.Contains("fix the thing", formatted);
    }

    private static FindingsDocument MakeFindingsDocument(SourceLocation location)
    {
        var evidence = new EvidenceItem("location", "Order.cs:15", "hot path", location);
        var finding = new Finding("f1", "TestAnalyzer", Severity.Warning, Confidence.High, "Title", "Summary", [evidence], []);
        return new FindingsDocument(
            FindingsDocument.CurrentSchemaVersion,
            new FindingsDumpSummary("dump.dmp", "net8.0", "x64", null, false),
            [finding]);
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {process.StandardError.ReadToEnd()}");
        }

        return stdout;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repoDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
