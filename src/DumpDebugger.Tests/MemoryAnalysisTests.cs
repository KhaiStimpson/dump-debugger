using System.Diagnostics;
using System.Text;
using DumpDebugger.Analysis;
using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Tests;

/// <summary>Opt-in for slow, large fixtures: set DUMPDEBUGGER_PERF=1.</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DUMPDEBUGGER_PERF") != "1")
        {
            Skip = "Performance fixture (~2 GB dump). Set DUMPDEBUGGER_PERF=1 to run.";
        }
    }
}

public class MemoryAnalysisTests
{
    [Fact]
    public async Task Leak_fixture_names_large_objects_by_owner_and_finds_their_root()
    {
        var dump = await FixtureDumps.GetAsync("leak");
        using var index = DumpIndex.Open(dump);
        var diagnosis = await new AnalysisPipeline(index, _ => Task.CompletedTask).RunAsync(true, CancellationToken.None);

        var largest = index.LargeObjects![0];
        Assert.Equal("PDF document \"Q3-statements\" in ReportCache", largest.Identity);

        var roots = index.Explorer!.FindRoots(new RootsQuery(largest.Address, RootPathMode.Shortest), CancellationToken.None);
        Assert.Equal(RootKind.Static, roots.Paths[0].Root.Kind);
        Assert.Equal("static ReportCache.s_entries", roots.Paths[0].Root.Label);

        var report = index.Explorer.Inspect(new ObjectQuery(Address(roots.Paths[0].Steps[^2].Object.Uri)));
        var expiry = report.Fields.Single(f => f.Name == "_expiry");
        Assert.Equal("null", expiry.Display);
        Assert.Equal("never expires", expiry.Note);

        var sb = new StringBuilder(Golden.Render(diagnosis));
        foreach (var lo in index.LargeObjects)
        {
            sb.AppendLine($"large: {lo.Identity} | {lo.TypeName} | {lo.Generation} | {lo.Detail}");
        }

        foreach (var step in roots.Paths[0].Steps)
        {
            sb.AppendLine($"path: {step.Object.Label} via {step.Via}");
        }

        Golden.Assert("leak", Golden.Normalize(sb.ToString()));
    }

    [PerfFact]
    public async Task Root_path_on_a_2GB_heap_takes_under_10_seconds_cold()
    {
        var dump = await FixtureDumps.GetAsync("bigheap");
        using var index = DumpIndex.Open(dump);
        var clock = Stopwatch.StartNew();

        // Cold: nothing analyzed yet, so this pays for the heap walk and the reference index.
        var explorer = await index.EnsureExplorerAsync(null, CancellationToken.None);
        var target = explorer.Index.Addresses[explorer.Index.Count / 2];
        var roots = explorer.FindRoots(new RootsQuery(target, RootPathMode.Shortest), CancellationToken.None);

        Assert.NotEmpty(roots.Paths);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Cold root path took {clock.Elapsed.TotalSeconds:F1}s.");
    }

    private static ulong Address(string uri) => EntityUri.TryGetAddress(uri, out var a) ? a : 0;
}
