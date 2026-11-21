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

    /// <summary>The primary runtime (see RuntimeSelection); DumpLoader orders it first.</summary>
    public ClrRuntime Runtime => Loaded.Runtimes.Count > 0
        ? Loaded.Runtimes[0]
        : throw new InvalidOperationException("The dump has no .NET runtime that could be loaded (DAC not found).");

    public bool HasRuntime => Loaded.Runtimes.Count > 0;

    public SourceLocator Sources { get; } = new();

    public IReadOnlyList<ThreadRecord>? Threads { get; private set; }

    public LockModel? Locks { get; private set; }

    public HeapModel? Heap { get; set; }

    public static DumpIndex Open(
        string path,
        bool allowSymbolServer = false,
        Action<string, string>? progress = null,
        CancellationToken ct = default,
        Core.Contracts.SymbolSettings? symbols = null)
    {
        var sw = Stopwatch.StartNew();
        var loaded = DumpLoader.Load(path, allowSymbolServer, progress, ct);
        var index = new DumpIndex(loaded, sw.Elapsed.TotalSeconds);
        index.Sources.Configure(symbols?.SymbolPaths, symbols?.SourcePaths);
        return index;
    }

    /// <summary>
    /// Applies new symbol and source folders to the open dump: PDBs are looked up again and the
    /// frames already read get their file and line refreshed, so nothing needs reopening.
    /// </summary>
    public async Task ConfigureSymbolsAsync(Core.Contracts.SymbolSettings symbols, CancellationToken ct)
    {
        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WithClrAsync(() =>
            {
                Sources.Configure(symbols.SymbolPaths, symbols.SourcePaths);
                if (Threads is { } threads)
                {
                    ThreadModelBuilder.Relocate(threads, Sources, ct);
                }

                return 0;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _modelGate.Release();
        }
    }

    /// <summary>The application's own modules and whether a PDB was found for each (framework modules are left out).</summary>
    public Task<IReadOnlyList<Core.Contracts.SymbolModule>> SymbolModulesAsync(CancellationToken ct) =>
        WithClrAsync<IReadOnlyList<Core.Contracts.SymbolModule>>(() => Runtime.EnumerateModules()
            .Where(m => !string.IsNullOrEmpty(m.Name) && FrameClassifier.KindOf("App.Type", FrameClassifier.ModuleName(m)) == Core.Contracts.FrameKind.App)
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => Sources.Describe(g.First()))
            .OrderBy(m => m.State == Core.Contracts.SymbolState.Loaded)
            .ThenBy(m => m.Module, StringComparer.OrdinalIgnoreCase)
            .Take(300)
            .ToList(), ct);

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

    public ObjectExplorer? Explorer { get; private set; }

    public IReadOnlyList<Core.Contracts.LargeObject>? LargeObjects { get; set; }

    public Core.Contracts.AsyncStacksResult? AsyncStacks { get; set; }

    /// <summary>The heap walk's aggregates, walking now if analysis hasn't yet (e.g. the diagnosis came from the workspace cache).</summary>
    public async Task<HeapModel> EnsureHeapAsync(CancellationToken ct)
    {
        if (Heap is { } h)
        {
            return h;
        }

        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Heap ??= await WithClrAsync(() => HeapWalker.Walk(Runtime, new HashSet<ulong>(), (_, _) => { }, ct, out _), ct).ConfigureAwait(false);
            return Heap;
        }
        finally
        {
            _modelGate.Release();
        }
    }

    /// <summary>The object explorer and its reference index, built on first use (seconds on large heaps; reported through <paramref name="progress"/>).</summary>
    public async Task<ObjectExplorer> EnsureExplorerAsync(Action<string, double>? progress, CancellationToken ct)
    {
        if (Explorer is { } e)
        {
            return e;
        }

        await _explorerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Explorer is null)
            {
                var heap = await EnsureHeapAsync(ct).ConfigureAwait(false);
                var index = await WithClrAsync(() => HeapIndex.Build(Runtime.Heap, heap.Table, progress ?? ((_, _) => { }), ct), ct).ConfigureAwait(false);
                Explorer = new ObjectExplorer(Runtime, index);
            }

            return Explorer;
        }
        finally
        {
            _explorerGate.Release();
        }
    }

    private readonly SemaphoreSlim _explorerGate = new(1, 1);

    public void Dispose()
    {
        Sources.Dispose();
        Loaded.Dispose();
    }
}
