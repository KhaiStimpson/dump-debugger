using DumpDebugger.Core.Source;

namespace DumpDebugger.Tests;

public class RepoUrlNormalizerTests
{
    [Theory]
    [InlineData("https://github.com/org/repo", "https://github.com/org/repo")]
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/repo")]
    [InlineData("https://github.com/org/repo/", "https://github.com/org/repo")]
    [InlineData("git@github.com:org/repo.git", "https://github.com/org/repo")]
    [InlineData("git@github.com:org/repo", "https://github.com/org/repo")]
    [InlineData("  https://github.com/org/repo  ", "https://github.com/org/repo")]
    public void Normalize_ProducesCanonicalHttpsForm(string raw, string expected)
    {
        Assert.Equal(expected, RepoUrlNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_HandlesNonGitHubHostsToo()
    {
        Assert.Equal(
            "https://ghe.internal.example.com/org/repo",
            RepoUrlNormalizer.Normalize("git@ghe.internal.example.com:org/repo.git"));
    }
}
