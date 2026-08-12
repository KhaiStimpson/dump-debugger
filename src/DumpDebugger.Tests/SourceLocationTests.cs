using DumpDebugger.Core.Source;

namespace DumpDebugger.Tests;

public class SourceLocationTests
{
    [Fact]
    public void ToGitHubBlobUrl_BuildsBlobUrlWithLineAnchor()
    {
        var location = new SourceLocation(
            "https://github.com/khaistimpson/dump-debugger",
            "abc123abc123abc123abc123abc123abc123abcd",
            "src/DumpDebugger.Core/Dump/ThreadInfo.cs",
            42,
            5);

        Assert.Equal(
            "https://github.com/khaistimpson/dump-debugger/blob/abc123abc123abc123abc123abc123abc123abcd/src/DumpDebugger.Core/Dump/ThreadInfo.cs#L42",
            location.ToGitHubBlobUrl());
    }

    [Fact]
    public void ResolveLocalPath_CombinesRepoRootWithRelativePath()
    {
        var repo = new RepoContext("/home/dev/repos/dump-debugger");
        var location = new SourceLocation("https://github.com/org/repo", new string('a', 40), "src/Foo.cs", 1, 1);

        var resolved = repo.ResolveLocalPath(location);

        Assert.EndsWith(Path.Combine("src", "Foo.cs"), resolved);
        Assert.StartsWith(repo.LocalPath, resolved);
    }
}
