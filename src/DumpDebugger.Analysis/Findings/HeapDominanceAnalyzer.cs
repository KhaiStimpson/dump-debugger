using DumpDebugger.Analysis.Frames;
using DumpDebugger.Core.Contracts;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// One type holding a large share of a large heap (PLAN.md §4.3 "Type dominance"). When a few
/// identified large objects make up most of that type, the finding names them ("mostly 3 PDF
/// documents in ReportCache"), which is what turns a statistic into something actionable.
/// Small heaps are skipped: "38% of a 6 MB heap" is noise, not a finding.
/// </summary>
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

            // Do a handful of identified instances account for most of this type?
            var big = (context.LargeObjects ?? []).Where(o => o.TypeName == type.Name).Take(5).ToList();
            var bigBytes = big.Sum(o => o.Size);
            var concentrated = big.Count > 0 && bigBytes >= type.Size * 0.5;
            var where = concentrated ? CommonHolder(big) : null;

            var title = $"{Code(type.Name)} is {Percent(share)} of the heap";
            if (concentrated)
            {
                var what = big.Select(o => Kind(o.Identity)).Distinct().Count() == 1 ? Kind(big[0].Identity) : "large objects";
                title += $", mostly {big.Count} {Pluralize(what, big.Count)}{(where is null ? "" : $" in {Code(where)}")}";
            }

            var severity = share >= 0.5 && heap.Overview.TotalBytes >= 1L << 30 ? Severity.Warning : Severity.Info;
            var evidence = new List<EntityRef> { EntityUri.TypeRef(type.Name, FrameClassifier.ShortTypeName(type.Name), $"{Bytes(type.Size)} · {Count(type.Count)} instances") };
            evidence.AddRange(big.Select(o => EntityUri.ObjectRef(o.Address, o.Identity, $"{o.TypeName} · {Bytes(o.Size)}")));

            var steps = new List<string>();
            if (concentrated)
            {
                steps.Add($"Open the largest one, {Chip(EntityUri.Object(big[0].Address))}, and check what keeps it alive (its path to a GC root).");
                steps.Add(where is null
                    ? "If those objects are cached, give the cache a size limit or an expiry."
                    : $"If {Code(where)} is a cache, give it a size limit or an expiry so entries can be evicted.");
            }
            else
            {
                steps.Add($"Open {Chip(EntityUri.Type(type.Name))} in Memory to see its largest instances.");
                steps.Add("Pick one and check what keeps it alive (its path to a GC root).");
            }

            yield return new AnalyzerResult(new Finding(
                Id: $"heap.dominance.{type.Name}",
                Analyzer: Name,
                Severity: severity,
                Confidence: Confidence.High,
                Title: title,
                Why: $"{Bytes(type.Size)} across {Plural((int)Math.Min(int.MaxValue, type.Count), "instance")}"
                     + (concentrated ? $" · top {big.Count} {Bytes(bigBytes)}" : ""),
                WhatItMeans: concentrated
                    ? $"Most of this memory is a few big objects rather than many small ones. That isn't a bug by itself, but objects this size live on the Large Object Heap and are only collected in full GCs, so if they're held longer than needed the process stays large."
                    : "One type holds a large share of managed memory. That isn't a bug by itself, but if memory is the problem, this is where to look first: either there are many more instances than expected, or a few are very large.",
                NextSteps: steps,
                Symptoms: [Symptom.HighMemory],
                Evidence: evidence,
                Score: Scores.For(severity, Confidence.High, share),
                Visual: FindingVisual.Heap));
        }
    }

    /// <summary>"PDF document "Q3" in ReportCache" → "PDF document".</summary>
    private static string Kind(string identity)
    {
        var quote = identity.IndexOf(" \"", StringComparison.Ordinal);
        var cut = quote >= 0 ? quote : identity.IndexOf(" in ", StringComparison.Ordinal);
        return cut > 0 ? identity[..cut] : identity;
    }

    private static string? CommonHolder(IReadOnlyList<LargeObject> objects)
    {
        var holders = objects.Select(o => o.Identity.LastIndexOf(" in ", StringComparison.Ordinal) is var i and >= 0 ? o.Identity[(i + 4)..] : null).Distinct().ToList();
        return holders.Count == 1 ? holders[0] : null;
    }

    private static string Pluralize(string noun, int n) => n == 1 || noun.EndsWith('s') || noun.EndsWith(']') ? noun : noun + "s";
}
