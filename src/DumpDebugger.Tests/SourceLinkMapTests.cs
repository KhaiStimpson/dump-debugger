using DumpDebugger.Analysis.SourceLink;

namespace DumpDebugger.Tests;

public class SourceLinkMapTests
{
    private const string SampleJson =
        """
        {
          "documents": {
            "C:\\src\\dump-debugger\\*": "https://raw.githubusercontent.com/khaistimpson/dump-debugger/abc123abc123abc123abc123abc123abc123abcd/*"
          }
        }
        """;

    [Fact]
    public void Resolve_MatchesLongestPrefix_AndSubstitutesSuffix()
    {
        var map = SourceLinkMap.TryParse(SampleJson);
        Assert.NotNull(map);

        var url = map!.Resolve(@"C:\src\dump-debugger\src\DumpDebugger.Analysis\ThreadEnumerator.cs");

        Assert.Equal(
            "https://raw.githubusercontent.com/khaistimpson/dump-debugger/abc123abc123abc123abc123abc123abc123abcd/src/DumpDebugger.Analysis/ThreadEnumerator.cs",
            url);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenDocumentOutsideAnyMappedPrefix()
    {
        var map = SourceLinkMap.TryParse(SampleJson);
        Assert.NotNull(map);

        Assert.Null(map!.Resolve(@"C:\other-repo\Foo.cs"));
    }

    [Fact]
    public void TryParse_ReturnsNull_ForMissingDocumentsProperty()
    {
        Assert.Null(SourceLinkMap.TryParse("{}"));
    }

    [Fact]
    public void TryParse_ReturnsNull_ForMalformedJson()
    {
        Assert.Null(SourceLinkMap.TryParse("not json"));
    }

    [Fact]
    public void TryParse_SkipsNonWildcardEntries()
    {
        const string json =
            """
            { "documents": { "C:\\exact\\Path.cs": "https://example.com/exact.cs" } }
            """;

        // No wildcard entries survive parsing, so the map itself is considered empty/unusable.
        Assert.Null(SourceLinkMap.TryParse(json));
    }
}
