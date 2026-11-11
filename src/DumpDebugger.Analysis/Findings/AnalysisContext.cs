using DumpDebugger.Analysis.Heap;
using DumpDebugger.Analysis.Locks;
using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Findings;

/// <summary>What analyzers can read. <see cref="Heap"/> is null until the heap walk has run.</summary>
public sealed class AnalysisContext
{
    public required ClrRuntime Runtime { get; init; }
    public required IReadOnlyList<ThreadRecord> Threads { get; init; }
    public required LockModel Locks { get; init; }
    public HeapModel? Heap { get; init; }

    /// <summary>Reference index and object queries; null until the large-object stage has run.</summary>
    public DumpDebugger.Analysis.Heap.ObjectExplorer? Explorer { get; init; }

    public IReadOnlyList<LargeObject>? LargeObjects { get; init; }

    public required Core.Dump.DumpMetadata Metadata { get; init; }

    public ThreadPoolStats? Pool { get; init; }

    public AsyncStacksResult? AsyncStacks { get; init; }

    public IReadOnlyDictionary<int, ThreadRecord> ThreadsById => _byId ??= Threads.ToDictionary(t => t.Id);

    private IReadOnlyDictionary<int, ThreadRecord>? _byId;

    public EntityRef ThreadRef(ThreadRecord t) => EntityUri.ThreadRef(t.Id, t.Request ?? t.WhereName);
}

/// <summary>A finding plus, optionally, how to say it as the headline if it ranks first.</summary>
public sealed record AnalyzerResult(Finding Finding, string? VerdictHeadline = null, string? VerdictBody = null);

public enum AnalyzerStage
{
    /// <summary>Needs threads and locks only: runs within seconds.</summary>
    Threads,

    /// <summary>Needs the heap walk.</summary>
    Heap,
}

/// <summary>
/// One deterministic analyzer. Explanations are authored here, per analyzer, and templated
/// from evidence: the analyzers carry the explanation, so no AI is needed to phrase it.
/// </summary>
public interface IFindingAnalyzer
{
    string Name { get; }

    AnalyzerStage Stage { get; }

    IEnumerable<AnalyzerResult> Analyze(AnalysisContext context);
}

public static class Scores
{
    public static double For(Severity severity, Confidence confidence, double magnitude01) =>
        severity switch { Severity.Critical => 100, Severity.Warning => 50, Severity.Info => 10, _ => 0 }
        + confidence switch { Confidence.High => 10, Confidence.Medium => 5, _ => 0 }
        + Math.Clamp(magnitude01, 0, 1) * 9;
}
