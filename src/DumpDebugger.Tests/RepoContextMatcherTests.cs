using DumpDebugger.Core.Source;

namespace DumpDebugger.Tests;

public class RepoContextMatcherTests
{
    private static SourceLocation Location(string repoUrl) => new(repoUrl, new string('a', 40), "src/Foo.cs", 1, 1);

    [Fact]
    public void Find_ReturnsNull_WhenNoRepoAssociated()
    {
        Assert.Null(RepoContextMatcher.Find([], Location("https://github.com/org/app")));
    }

    [Fact]
    public void Find_ReturnsExactUrlMatch_AmongSeveral()
    {
        RepoContext[] repos =
        [
            new RepoContext("/repos/app", "https://github.com/org/app"),
            new RepoContext("/repos/packages", "https://github.com/org/internal-packages"),
        ];

        var match = RepoContextMatcher.Find(repos, Location("https://github.com/org/internal-packages"));

        Assert.Same(repos[1], match);
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        RepoContext[] repos = [new RepoContext("/repos/app", "https://github.com/Org/App")];

        var match = RepoContextMatcher.Find(repos, Location("https://github.com/org/app"));

        Assert.Same(repos[0], match);
    }

    [Fact]
    public void Find_FallsBackToTheOnlyRepo_WhenItsUrlDoesNotMatchOrWasNeverDetected()
    {
        RepoContext[] repos = [new RepoContext("/repos/app", null)];

        var match = RepoContextMatcher.Find(repos, Location("https://github.com/org/app"));

        Assert.Same(repos[0], match);
    }

    [Fact]
    public void Find_ReturnsNull_WhenSeveralReposAssociatedButNoneMatch()
    {
        RepoContext[] repos =
        [
            new RepoContext("/repos/a", "https://github.com/org/a"),
            new RepoContext("/repos/b", "https://github.com/org/b"),
        ];

        var match = RepoContextMatcher.Find(repos, Location("https://github.com/org/c"));

        Assert.Null(match);
    }
}
