using System.Diagnostics;
using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Heap;
using DumpDebugger.Analysis.Locks;
using DumpDebugger.Analysis.Threads;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis;

/// <summary>
/// One open dump inside the worker, plus everything computed from it so far. ClrMD reads go
/// through <see cref="WithClrAsync{T}"/> one at a time; queries over already-computed models
/// (threads, locks) don't need ClrMD, so they answer immediately even while the heap is being
/// walked, which is what lets the UI show threads before the heap stage finishes.
/// </summary>
public sealed class DumpIndex : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _modelGate = new(1, 1);

    private DumpIndex(LoadedDump loaded, double openSeconds)
    {
        Loaded = loaded;
        OpenSeconds = openSeconds;
    }

    public LoadedDump Loaded { get; }

    public double OpenSeconds { get; }

    public ClrRuntime Runtime => Loaded.Runtimes.Count > 0
        ? Loaded.Runtimes[0]
        : throw new InvalidOperationException("The dump has no .NET runtime that could be loaded (DAC not found).");

    public bool HasRuntime => Loaded.Runtimes.Count > 0;

    public SourceLocator Sources { get; } = new();

    public IReadOnlyList<ThreadRecord>? Threads { get; private set; }

    public LockModel? Locks { get; private set; }

    public HeapModel? Heap { get; set; }

    public static DumpIndex Open(string path)
    {
        var sw = Stopwatch.StartNew();
        var loaded = DumpLoader.Load(path, allowSymbolServer: false);
        return new DumpIndex(loaded, sw.Elapsed.TotalSeconds);
    }

    public async Task<T> WithClrAsync<T>(Func<T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(work, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Threads and locks, computed on first use (by the pipeline or by a direct query).</summary>
    public async Task<(IReadOnlyList<ThreadRecord> Threads, LockModel Locks)> EnsureThreadsAsync(CancellationToken ct)
    {
        if (Threads is { } t && Locks is { } l)
        {
            return (t, l);
        }

        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Threads is null || Locks is null)
            {
                var threads = await WithClrAsync(() => ThreadModelBuilder.Build(Runtime, Sources, ct), ct).ConfigureAwait(false);
                var locks = await WithClrAsync(() => LockModelBuilder.Build(Runtime, threads, ct), ct).ConfigureAwait(false);
                Threads = threads;
                Locks = locks;
            }

            return (Threads, Locks);
        }
        finally
        {
            _modelGate.Release();
        }
    }

    public void Dispose()
    {
        Sources.Dispose();
        Loaded.Dispose();
    }
}
