using System.Diagnostics;
using DumpDebugger.Analysis.Findings;
using DumpDebugger.Analysis.Heap;
using DumpDebugger.Analysis.Locks;
using DumpDebugger.Core.Contracts;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis;

/// <summary>
/// Runs analysis as named stages and publishes a full <see cref="Diagnosis"/> snapshot after
/// each one (and periodically during the heap walk). Thread and lock findings, which decide
/// most hangs, arrive within seconds; heap-based analyzers stream in after.
/// </summary>
public sealed class AnalysisPipeline
{
    private readonly DumpIndex _index;
    private readonly Func<Diagnosis, Task> _publish;
    private readonly List<IFindingAnalyzer> _analyzers;
    private readonly Dictionary<string, AnalysisStage> _stages = new();
    private readonly List<string> _stageOrder = [];
    private readonly List<AnalyzerResult> _results = [];
    private readonly Stopwatch _clock = new();
    private AnalysisContext? _context;
    private HeapOverview? _overview;
    private ThreadPoolStats? _pool;

    public AnalysisPipeline(DumpIndex index, Func<Diagnosis, Task> publish, IEnumerable<IFindingAnalyzer>? analyzers = null)
    {
        _index = index;
        _publish = publish;
        _analyzers = (analyzers ?? AnalyzerCatalog.All).ToList();
    }

    public async Task<Diagnosis> RunAsync(bool includeHeap, CancellationToken ct)
    {
        _clock.Start();
        var dacTier = _index.Loaded.Metadata.Runtimes.FirstOrDefault()?.DacTier;
        SetStage("runtime", "Matched runtime & resolved DAC", StageStatus.Done, _index.OpenSeconds, DacLabel(dacTier));
        SetStage("threads", "Reading threads & stacks", StageStatus.Running);
        SetStage("locks", "Locks & wait-for graph", StageStatus.Pending);
        if (includeHeap)
        {
            SetStage("heap", "Walking the heap", StageStatus.Pending);
            SetStage("analyzers", "Thread-pool, async & exception analyzers", StageStatus.Pending);
        }

        await PublishAsync(false).ConfigureAwait(false);

        // Threads & locks.
        var t0 = _clock.Elapsed.TotalSeconds;
        var (threads, locks) = await _index.EnsureThreadsAsync(ct).ConfigureAwait(false);
        SetStage("threads", $"Read {Plural(threads.Count, "thread")} & stacks", StageStatus.Done, _clock.Elapsed.TotalSeconds - t0);
        var cycles = locks.Cycles.Count;
        var contended = locks.Locks.Values.Count(l => l.Waiters.Count > 0);
        SetStage("locks", "Locks & wait-for graph", StageStatus.Done, 0,
            cycles > 0 ? $"{Plural(cycles, "cycle")} found" : contended > 0 ? $"{Plural(contended, "contended lock")}" : "no contention",
            tone: cycles > 0 ? Tone.Critical : Tone.Neutral);

        _overview = await _index.WithClrAsync(() => HeapWalker.QuickOverview(_index.Runtime.Heap), ct).ConfigureAwait(false);
        _context = new AnalysisContext { Runtime = _index.Runtime, Threads = threads, Locks = locks };
        RunAnalyzers(AnalyzerStage.Threads);
        await PublishAsync(false).ConfigureAwait(false);

        if (includeHeap)
        {
            var heap = await WalkHeapAsync(locks, ct).ConfigureAwait(false);
            _index.Heap = heap;
            _overview = heap.Overview;
            _context = new AnalysisContext { Runtime = _index.Runtime, Threads = threads, Locks = locks, Heap = heap };

            var ta = _clock.Elapsed.TotalSeconds;
            SetStage("analyzers", "Thread-pool, async & exception analyzers", StageStatus.Running);
            await PublishAsync(false).ConfigureAwait(false);
            await _index.WithClrAsync(() =>
            {
                RunAnalyzers(AnalyzerStage.Heap);
                return 0;
            }, ct).ConfigureAwait(false);
            SetStage("analyzers", "Thread-pool, async & exception analyzers", StageStatus.Done, _clock.Elapsed.TotalSeconds - ta,
                $"{Plural(_analyzers.Count, "analyzer")} ran");
        }

        return await PublishAsync(true).ConfigureAwait(false);
    }

    private async Task<HeapModel> WalkHeapAsync(LockModel locks, CancellationToken ct)
    {
        var th = _clock.Elapsed.TotalSeconds;
        SetStage("heap", "Walking the heap", StageStatus.Running, null, null, 0);
        await PublishAsync(false).ConfigureAwait(false);

        var unnamed = locks.Locks.Values.Where(l => !l.Named).Select(l => l.Address).ToHashSet();
        var lastPublish = Stopwatch.StartNew();
        Dictionary<ulong, string> names = [];
        var heap = await _index.WithClrAsync(() => HeapWalker.Walk(_index.Runtime, unnamed, (done, total) =>
        {
            SetStage("heap", "Walking the heap", StageStatus.Running, _clock.Elapsed.TotalSeconds - th,
                $"{Bytes(done)} / {Bytes(total)}", total == 0 ? 1 : (double)done / total);
            if (lastPublish.ElapsedMilliseconds > 300)
            {
                lastPublish.Restart();
                _ = PublishAsync(false);
            }
        }, ct, out names), ct).ConfigureAwait(false);

        LockModelBuilder.ApplyHeapNames(locks, names);
        SetStage("heap", "Walked the heap", StageStatus.Done, _clock.Elapsed.TotalSeconds - th,
            $"{Bytes(heap.Overview.TotalBytes)} · {Count(heap.Overview.ObjectCount)} objects", 1);
        return heap;
    }

    private void RunAnalyzers(AnalyzerStage stage)
    {
        foreach (var analyzer in _analyzers.Where(a => a.Stage == stage))
        {
            try
            {
                _results.AddRange(analyzer.Analyze(_context!));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken analyzer must not sink the diagnosis; say so honestly instead.
                _results.Add(new AnalyzerResult(new Finding(
                    $"analyzer-failed.{analyzer.Name}", analyzer.Name, Severity.Info, Confidence.Low,
                    $"The {analyzer.Name} analyzer couldn't finish",
                    ex.Message,
                    "This dump has a shape the analyzer didn't expect, so its checks are missing from this diagnosis. Other findings are unaffected.",
                    ["Treat the absence of this analyzer's findings as unknown, not as healthy."],
                    [], [], 1)));
            }
        }
    }

    private Task<Diagnosis> PublishAsync(bool complete) => PublishCoreAsync(complete);

    private async Task<Diagnosis> PublishCoreAsync(bool complete)
    {
        var ranked = _results.OrderByDescending(r => r.Finding.Score).ToList();
        var md = _index.Loaded.Metadata;
        var rt = md.Runtimes.FirstOrDefault();
        var summary = new DumpSummary(
            md.DumpPath,
            Path.GetFileName(md.DumpPath),
            md.FileSizeBytes,
            rt is null ? "unknown runtime" : $"{(rt.Family == Core.Dump.RuntimeFamily.NetFramework ? ".NET Fx" : ".NET")} {rt.ProductVersion}",
            md.Architecture.ToString().ToLowerInvariant(),
            _context?.Threads.Count ?? md.ThreadCount,
            md.ProcessName,
            DacLabel(rt?.DacTier));

        Verdict verdict;
        IReadOnlyList<Vital> vitals;
        IReadOnlyList<RequestCount> busiest;
        if (_context is not null && _overview is not null)
        {
            verdict = VerdictSynthesizer.Synthesize(ranked, _context, complete);
            vitals = VitalsBuilder.Build(_context, _overview, _pool);
            busiest = _context.Threads.Where(t => t.Request is not null)
                .GroupBy(t => t.Request!).Select(g => new RequestCount(g.Key, g.Count()))
                .OrderByDescending(r => r.Threads).ThenBy(r => r.Route).Take(6).ToList();
        }
        else
        {
            verdict = new Verdict(Severity.Ok, Confidence.Low, "Analyzing…", "Reading threads and locks.", [], null);
            vitals = [];
            busiest = [];
        }

        var diagnosis = new Diagnosis(
            Diagnosis.CurrentSchemaVersion,
            summary,
            verdict,
            vitals,
            ranked.Select(r => r.Finding).ToList(),
            busiest,
            StageSnapshot(),
            _index.OpenSeconds + _clock.Elapsed.TotalSeconds,
            complete,
            FromCache: false,
            DateTimeOffset.UtcNow);

        await _publish(diagnosis).ConfigureAwait(false);
        return diagnosis;
    }

    public void SetThreadPoolStats(ThreadPoolStats stats) => _pool = stats;

    private List<AnalysisStage> StageSnapshot()
    {
        lock (_stages)
        {
            return _stageOrder.Select(id => _stages[id]).ToList();
        }
    }

    private void SetStage(string id, string label, StageStatus status, double? seconds = null, string? detail = null, double? fraction = null, Tone tone = Tone.Neutral)
    {
        lock (_stages)
        {
            if (!_stages.ContainsKey(id))
            {
                _stageOrder.Add(id);
            }

            _stages[id] = new AnalysisStage(id, label, status, seconds, detail, fraction, tone);
        }
    }

    private static string? DacLabel(Core.Dump.DacResolutionTier? tier) => tier switch
    {
        Core.Dump.DacResolutionTier.EmbeddedInDump => "embedded in dump",
        Core.Dump.DacResolutionTier.LocalMachine => "local machine",
        Core.Dump.DacResolutionTier.AppCache => "local cache",
        Core.Dump.DacResolutionTier.SymbolServer => "symbol server",
        _ => null,
    };
}

public static class AnalyzerCatalog
{
    public static IReadOnlyList<IFindingAnalyzer> All =>
    [
        new DeadlockAnalyzer(),
        new ContentionAnalyzer(),
        new HeapDominanceAnalyzer(),
    ];
}
