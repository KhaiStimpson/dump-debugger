using DumpDebugger.Analysis.SourceLink;

namespace DumpDebugger.Tests;

public class SourceLocationUrlParserTests
{
    [Fact]
    public void TryParse_GitHubRawUrl_NormalizesRepoUrlAndExtractsShaAndPath()
    {
        const string url =
            "https://raw.githubusercontent.com/khaistimpson/dump-debugger/abc123abc123abc123abc123abc123abc123abcd/src/DumpDebugger.Core/Dump/ThreadInfo.cs";

        var ok = SourceLocationUrlParser.TryParse(url, out var repoUrl, out var sha, out var relativePath);

        Assert.True(ok);
        Assert.Equal("https://github.com/khaistimpson/dump-debugger", repoUrl);
        Assert.Equal("abc123abc123abc123abc123abc123abc123abcd", sha);
        Assert.Equal("src/DumpDebugger.Core/Dump/ThreadInfo.cs", relativePath);
    }

    [Fact]
    public void TryParse_ReturnsFalse_WhenNoCommitShaSegmentPresent()
    {
        var ok = SourceLocationUrlParser.TryParse("https://example.com/not-a-source-link-url", out _, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_ReturnsFalse_ForShaSegmentShorterThan40Chars()
    {
        var ok = SourceLocationUrlParser.TryParse("https://example.com/deadbeef/file.cs", out _, out _, out _);

        Assert.False(ok);
    }
}
