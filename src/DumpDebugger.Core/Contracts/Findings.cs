using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Core.Contracts;

public enum Severity
{
    Ok,
    Info,
    Warning,
    Critical,
}

public enum Confidence
{
    Low,
    Medium,
    High,
}

/// <summary>What the on-call developer saw; "What were you seeing?" chips re-rank findings by these.</summary>
public enum Symptom
{
    Hang,
    HighMemory,
    Crash,
    SlowCpu,
}

/// <summary>
/// Finding v2. Every text field is authored per analyzer and templated from evidence, never
/// free-form. Text supports two inline marks: <c>`code`</c>, and <c>{{uri}}</c> which renders
/// as the entity chip for that URI (its label comes from <see cref="Evidence"/>).
/// </summary>
[TsDoc("Finding v2. Text fields support `code` and {{uri}} (an inline entity chip whose label comes from `evidence`).")]
public sealed record Finding(
    string Id,
    string Analyzer,
    Severity Severity,
    Confidence Confidence,
    string Title,
    string Why,
    string WhatItMeans,
    IReadOnlyList<string> NextSteps,
    IReadOnlyList<Symptom> Symptoms,
    IReadOnlyList<EntityRef> Evidence,
    double Score,
    FindingVisual Visual = FindingVisual.None);

/// <summary>A picture the Diagnosis page can draw next to a finding.</summary>
public enum FindingVisual
{
    None,
    WaitGraph,
    Heap,
}

/// <summary>The headline: one plain sentence plus a short body, picked and phrased from the top findings.</summary>
public sealed record Verdict(
    Severity Severity,
    Confidence Confidence,
    string Headline,
    string Body,
    IReadOnlyList<EntityRef> Entities,
    string? FindingId);

public enum Tone
{
    Neutral,
    Accent,
    Ok,
    Info,
    Warning,
    Critical,
}

/// <summary>One cell of the vitals strip.</summary>
public sealed record Vital(string Key, string Label, string Value, string Detail, double? Fraction, Tone Tone);

public enum StageStatus
{
    Pending,
    Running,
    Done,
    Skipped,
    Failed,
}

/// <summary>A named analysis step, shown on the Analyzing screen ("never a bare spinner").</summary>
public sealed record AnalysisStage(
    string Id,
    string Label,
    StageStatus Status,
    double? Seconds,
    string? Detail,
    double? Fraction,
    Tone Tone = Tone.Neutral);

public sealed record RequestCount(string Route, int Threads);

public sealed record DumpSummary(
    string Path,
    string FileName,
    long FileSizeBytes,
    string Runtime,
    string Architecture,
    int ThreadCount,
    string? ProcessName,
    string? DacSource);

/// <summary>
/// Everything the Diagnosis page needs, in one document. The worker pushes a fresh snapshot
/// after every stage (<c>analysis.updated</c>), so early findings show while the heap is still
/// being walked; the final one (<see cref="Complete"/>) is cached in the workspace.
/// </summary>
public sealed record Diagnosis(
    int SchemaVersion,
    DumpSummary Dump,
    Verdict Verdict,
    IReadOnlyList<Vital> Vitals,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<RequestCount> BusiestRequests,
    IReadOnlyList<AnalysisStage> Stages,
    double Seconds,
    bool Complete,
    bool FromCache,
    DateTimeOffset AnalyzedAt)
{
    public const int CurrentSchemaVersion = 2;
}
