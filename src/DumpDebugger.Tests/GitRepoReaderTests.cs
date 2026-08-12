using System.Diagnostics;
using DumpDebugger.Llm;

namespace DumpDebugger.Tests;

/// <summary>
/// Exercises GitRepoReader against a throwaway repo created for the test (git init + one
/// commit in a temp directory) rather than this solution's own repo, so the test doesn't
/// depend on being run from a particular working directory or on this repo's own history.
/// </summary>
public sealed class GitRepoReaderTests : IDisposable
{
    private readonly string _repoDir = Directory.CreateTempSubdirectory("dumpdebugger-git-test-").FullName;
    private string _commitSha = "";

    public GitRepoReaderTests()
    {
        RunGit(_repoDir, "init -q");
        RunGit(_repoDir, "config user.email test@example.com");
        RunGit(_repoDir, "config user.name Test");

        File.WriteAllText(Path.Combine(_repoDir, "hello.cs"), "class Hello\n{\n    void Say() { }\n}\n");
        RunGit(_repoDir, "add hello.cs");
        RunGit(_repoDir, "commit -q -m initial");

        _commitSha = RunGit(_repoDir, "rev-parse HEAD").Trim();
    }

    [Fact]
    public async Task TryReadFileAtCommitAsync_ReturnsFileContentAtThatCommit()
    {
        var content = await GitRepoReader.TryReadFileAtCommitAsync(_repoDir, _commitSha, "hello.cs", CancellationToken.None);

        Assert.NotNull(content);
        Assert.Contains("void Say()", content);
    }

    [Fact]
    public async Task TryReadFileAtCommitAsync_ReturnsNull_ForUnknownPath()
    {
        var content = await GitRepoReader.TryReadFileAtCommitAsync(_repoDir, _commitSha, "does-not-exist.cs", CancellationToken.None);

        Assert.Null(content);
    }

    [Fact]
    public async Task TryReadFileAtCommitAsync_ReturnsNull_ForUnknownCommit()
    {
        var content = await GitRepoReader.TryReadFileAtCommitAsync(_repoDir, new string('0', 40), "hello.cs", CancellationToken.None);

        Assert.Null(content);
    }

    [Fact]
    public async Task TryReadFileAtCommitAsync_ReturnsNull_ForNonRepoDirectory()
    {
        var notARepo = Directory.CreateTempSubdirectory("dumpdebugger-not-a-repo-").FullName;
        try
        {
            var content = await GitRepoReader.TryReadFileAtCommitAsync(notARepo, _commitSha, "hello.cs", CancellationToken.None);
            Assert.Null(content);
        }
        finally
        {
            Directory.Delete(notARepo, recursive: true);
        }
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
