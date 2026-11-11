using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Heap;

/// <summary>
/// Every live object as parallel arrays in address order, filled during the heap walk so the
/// reference index never needs a second walk. About 21 bytes per object.
/// </summary>
public sealed class ObjectTable(int estimate)
{
    private readonly Dictionary<ulong, int> _typeIdByMt = [];
    private ulong[] _addresses = new ulong[estimate];
    private int[] _typeIds = new int[estimate];
    private long[] _sizes = new long[estimate];
    private byte[] _gens = new byte[estimate];

    public int Count { get; private set; }

    public List<ClrType> Types { get; } = [];

    public void Add(ulong address, ClrType type, long size, byte generation)
    {
        if (Count == _addresses.Length)
        {
            var grow = Math.Max(1024, _addresses.Length + (_addresses.Length / 2));
            Array.Resize(ref _addresses, grow);
            Array.Resize(ref _typeIds, grow);
            Array.Resize(ref _sizes, grow);
            Array.Resize(ref _gens, grow);
        }

        if (!_typeIdByMt.TryGetValue(type.MethodTable, out var tid))
        {
            tid = Types.Count;
            Types.Add(type);
            _typeIdByMt[type.MethodTable] = tid;
        }

        _addresses[Count] = address;
        _typeIds[Count] = tid;
        _sizes[Count] = size;
        _gens[Count] = generation;
        Count++;
    }

    /// <summary>Trims the arrays to size and hands them over (the table is not used afterwards).</summary>
    public (ulong[] Addresses, int[] TypeIds, long[] Sizes, byte[] Gens) Seal()
    {
        Array.Resize(ref _addresses, Count);
        Array.Resize(ref _typeIds, Count);
        Array.Resize(ref _sizes, Count);
        Array.Resize(ref _gens, Count);
        return (_addresses, _typeIds, _sizes, _gens);
    }
}

/// <summary>
/// Compact, read-only index of every live object plus the reverse-reference graph, built the
/// first time something needs "who references this?" (referrers, GC-root paths, owner-aware
/// naming, instances of a type). Objects are numbered by address; reverse references are stored
/// CSR-style as int arrays (<see cref="RefStart"/>/<see cref="RefSources"/>), so N objects with
/// E references cost about 21·N + 4·(N + E) bytes rather than a collection per object.
/// </summary>
public sealed class HeapIndex
{
    private HeapIndex(ulong[] addresses, int[] typeIds, long[] sizes, byte[] generations, List<ClrType> types, int[] refStart, int[] refSources)
    {
        Addresses = addresses;
        TypeIds = typeIds;
        Sizes = sizes;
        Generations = generations;
        Types = types;
        RefStart = refStart;
        RefSources = refSources;
    }

    public ulong[] Addresses { get; }

    public int[] TypeIds { get; }

    public long[] Sizes { get; }

    /// <summary>0,1,2 = gen; 3 = LOH; 4 = POH; 5 = frozen.</summary>
    public byte[] Generations { get; }

    public List<ClrType> Types { get; }

    public int[] RefStart { get; }

    public int[] RefSources { get; }

    public int Count => Addresses.Length;

    public long EdgeCount => RefSources.LongLength;

    public int IndexOf(ulong address)
    {
        var i = Array.BinarySearch(Addresses, address);
        return i >= 0 ? i : -1;
    }

    public ReadOnlySpan<int> ReferrersOf(int index) => RefSources.AsSpan(RefStart[index], RefStart[index + 1] - RefStart[index]);

    public ClrType TypeOf(int index) => Types[TypeIds[index]];

    public static string GenerationName(byte g) => g switch
    {
        0 => "Gen0",
        1 => "Gen1",
        2 => "Gen2",
        3 => "LOH",
        4 => "POH",
        5 => "Frozen",
        _ => "?",
    };

    public static byte GenerationOf(ClrSegment seg, ulong address) => seg.Kind switch
    {
        GCSegmentKind.Generation0 => 0,
        GCSegmentKind.Generation1 => 1,
        GCSegmentKind.Generation2 => 2,
        GCSegmentKind.Large => 3,
        GCSegmentKind.Pinned => 4,
        GCSegmentKind.Frozen => 5,
        GCSegmentKind.Ephemeral => seg.Generation0.Contains(address) ? (byte)0 : seg.Generation1.Contains(address) ? (byte)1 : (byte)2,
        _ => 2,
    };

    /// <summary>
    /// Builds the reverse-reference graph over the walk's object table. The reference pass is
    /// split across cores when ClrMD reports the runtime as thread-safe; each chunk collects its
    /// own edges, then one counting sort produces the CSR arrays.
    /// </summary>
    public static HeapIndex Build(ClrHeap heap, ObjectTable table, Action<string, double> progress, CancellationToken ct)
    {
        var types = table.Types;
        var (addr, tids, sizes, gens) = table.Seal();
        var n = addr.Length;
        var workers = heap.Runtime.IsThreadSafe ? Math.Clamp(Environment.ProcessorCount, 1, 16) : 1;
        var chunk = (n + workers - 1) / Math.Max(1, workers);
        var edges = new (int[] Src, int[] Dst, int Count)[workers];
        long processed = 0;

        void Run(int w)
        {
            var from = w * chunk;
            var to = Math.Min(n, from + chunk);
            var src = new int[Math.Max(16, (to - from) * 2)];
            var dst = new int[src.Length];
            var count = 0;
            for (var i = from; i < to; i++)
            {
                if ((i & 0x3fff) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = Interlocked.Add(ref processed, 0x4000);
                    if (w == 0)
                    {
                        progress("Indexing references", Math.Min(1, (double)p / Math.Max(1, n)));
                    }
                }

                var type = types[tids[i]];
                if (!type.ContainsPointers)
                {
                    continue;
                }

                foreach (var target in heap.GetObject(addr[i], type).EnumerateReferenceAddresses(carefully: false, considerDependantHandles: false))
                {
                    var j = Array.BinarySearch(addr, target);
                    if (j < 0 || j == i)
                    {
                        continue;
                    }

                    if (count == src.Length)
                    {
                        Array.Resize(ref src, src.Length * 2);
                        Array.Resize(ref dst, dst.Length * 2);
                    }

                    src[count] = i;
                    dst[count] = j;
                    count++;
                }
            }

            edges[w] = (src, dst, count);
        }

        if (workers == 1)
        {
            Run(0);
        }
        else
        {
            Parallel.For(0, workers, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = workers }, Run);
        }

        var start = new int[n + 1];
        foreach (var (_, dst, count) in edges)
        {
            for (var e = 0; e < count; e++)
            {
                start[dst[e] + 1]++;
            }
        }

        for (var i = 0; i < n; i++)
        {
            start[i + 1] += start[i];
        }

        var fill = (int[])start.Clone();
        var sources = new int[start[n]];
        foreach (var (src, dst, count) in edges)
        {
            for (var e = 0; e < count; e++)
            {
                sources[fill[dst[e]]++] = src[e];
            }
        }

        progress("Indexed", 1);
        return new HeapIndex(addr, tids, sizes, gens, types, start, sources);
    }
}
