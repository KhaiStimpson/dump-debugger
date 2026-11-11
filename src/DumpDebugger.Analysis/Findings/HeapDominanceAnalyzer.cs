using DumpDebugger.Core.Contracts;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>One type holding a large share of a large heap (PLAN.md §4.3 "Type dominance").
/// Small heaps are skipped: "38% of a 6 MB heap" is noise, not a finding.</summary>
public sealed class HeapDominanceAnalyzer : IFindingAnalyzer
{
    private const long MinHeapBytes = 256L * 1024 * 1024;
    private const double MinShare = 0.30;

    public string Name => "heap-dominance";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        var heap = context.Heap;
        if (heap is null || heap.Overview.TotalBytes < MinHeapBytes)
        {
            yield break;
        }

        foreach (var type in heap.Types.Take(3))
        {
            var share = (double)type.Size / heap.Overview.TotalBytes;
            if (share < MinShare)
            {
                continue;
            }

            var severity = share >= 0.5 && heap.Overview.TotalBytes >= 1L << 30 ? Severity.Warning : Severity.Info;
            yield return new AnalyzerResult(new Finding(
                Id: $"heap.dominance.{type.Name}",
                Analyzer: Name,
                Severity: severity,
                Confidence: Confidence.High,
                Title: $"{Code(type.Name)} is {Percent(share)} of the heap",
                Why: $"{Bytes(type.Size)} across {Plural((int)Math.Min(int.MaxValue, type.Count), "instance")}",
                WhatItMeans: "One type holds a large share of managed memory. That isn't a bug by itself, but if memory is the problem, this is where to look first: either there are many more instances than expected, or a few are very large.",
                NextSteps:
                [
                    $"Open {Chip(EntityUri.Type(type.Name))} in Memory to see its largest instances.",
                    "Pick one and check what keeps it alive (its path to a GC root).",
                ],
                Symptoms: [Symptom.HighMemory],
                Evidence: [EntityUri.TypeRef(type.Name, detail: $"{Bytes(type.Size)} · {Count(type.Count)} instances")],
                Score: Scores.For(severity, Confidence.High, share),
                Visual: FindingVisual.Heap));
        }
    }
}
