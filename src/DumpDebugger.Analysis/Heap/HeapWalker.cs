using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Locks;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Heap;

public sealed class TypeAggregate
{
    public required ulong MethodTable { get; init; }
    public required string Name { get; init; }
    public long Count { get; set; }
    public long Size { get; set; }
    public long LohSize { get; set; }
}

public sealed class HeapModel
{
    public required HeapOverview Overview { get; init; }
    public required IReadOnlyList<TypeAggregate> Types { get; init; }
    public required long FreeBytes { get; init; }
    public required long LohFreeBytes { get; init; }
    public required long LohBytes { get; init; }

    public IEnumerable<TypeStat> TypeStats => Types.Select(t => new TypeStat(t.Name, t.Count, t.Size));
}

/// <summary>
/// One pass over every object, segment by segment, with byte-accurate progress
/// ("Walking the heap · 1.48 / 2.38 GB"). Aggregates by method table, so the walk never
/// materializes a type name per object, and never ships objects anywhere: only aggregates
/// leave the worker.
/// </summary>
public static class HeapWalker
{
    public static HeapOverview QuickOverview(ClrHeap heap)
    {
        var gens = new Dictionary<string, long>();
        long total = 0;
        foreach (var seg in heap.Segments)
        {
            total += (long)seg.ObjectRange.Length;
            foreach (var (name, bytes) in GenerationsOf(seg))
            {
                gens[name] = gens.GetValueOrDefault(name) + bytes;
            }
        }

        return new HeapOverview(total, 0, 0, OrderGenerations(gens), Walked: false);
    }

    public static HeapModel Walk(
        ClrRuntime runtime,
        IReadOnlySet<ulong> unnamedLocks,
        Action<long, long> progress,
        CancellationToken ct,
        out Dictionary<ulong, string> lockNames)
    {
        var heap = runtime.Heap;
        var types = new Dictionary<ulong, TypeAggregate>();
        lockNames = [];
        var segments = heap.Segments.ToList();
        var totalBytes = segments.Sum(s => (long)s.ObjectRange.Length);
        long done = 0;
        long objects = 0;
        long free = 0;
        long lohFree = 0;
        long lohBytes = 0;
        var gens = new Dictionary<string, long>();
        var lastReport = 0L;

        foreach (var seg in segments)
        {
            var isLoh = seg.Kind == GCSegmentKind.Large;
            foreach (var (name, bytes) in GenerationsOf(seg))
            {
                gens[name] = gens.GetValueOrDefault(name) + bytes;
            }

            foreach (var obj in seg.EnumerateObjects())
            {
                if ((objects & 0x3fff) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var position = done + (long)(obj.Address - seg.ObjectRange.Start);
                    if (position - lastReport > 4 * 1024 * 1024)
                    {
                        progress(position, totalBytes);
                        lastReport = position;
                    }
                }

                if (!obj.IsValid || obj.Type is null)
                {
                    continue;
                }

                var size = (long)obj.Size;
                if (obj.IsFree)
                {
                    free += size;
                    if (isLoh)
                    {
                        lohFree += size;
                    }

                    continue;
                }

                objects++;
                if (!types.TryGetValue(obj.Type.MethodTable, out var agg))
                {
                    agg = new TypeAggregate { MethodTable = obj.Type.MethodTable, Name = obj.Type.Name ?? "<unknown type>" };
                    types[obj.Type.MethodTable] = agg;
                }

                agg.Count++;
                agg.Size += size;
                if (isLoh)
                {
                    agg.LohSize += size;
                    lohBytes += size;
                }

                if (unnamedLocks.Count > lockNames.Count && obj.Type.ContainsPointers)
                {
                    foreach (var reference in obj.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: false))
                    {
                        if (unnamedLocks.Contains(reference.Object.Address) && reference.Field is { } field && !lockNames.ContainsKey(reference.Object.Address))
                        {
                            lockNames[reference.Object.Address] = $"{FrameClassifier.ShortTypeName(obj.Type.Name)}.{LockModelBuilder.CleanFieldName(field.Name)}";
                        }
                    }
                }
            }

            done += (long)seg.ObjectRange.Length;
            progress(done, totalBytes);
        }

        var sorted = types.Values.OrderByDescending(t => t.Size).ToList();
        var walkedTotal = sorted.Sum(t => t.Size);
        return new HeapModel
        {
            Overview = new HeapOverview(walkedTotal, objects, sorted.Count, OrderGenerations(gens), Walked: true),
            Types = sorted,
            FreeBytes = free,
            LohFreeBytes = lohFree,
            LohBytes = lohBytes,
        };
    }

    private static IEnumerable<(string, long)> GenerationsOf(ClrSegment seg)
    {
        switch (seg.Kind)
        {
            case GCSegmentKind.Large:
                yield return ("LOH", (long)seg.ObjectRange.Length);
                break;
            case GCSegmentKind.Pinned:
                yield return ("POH", (long)seg.ObjectRange.Length);
                break;
            case GCSegmentKind.Frozen:
                yield return ("Frozen", (long)seg.ObjectRange.Length);
                break;
            case GCSegmentKind.Generation0:
                yield return ("Gen0", (long)seg.ObjectRange.Length);
                break;
            case GCSegmentKind.Generation1:
                yield return ("Gen1", (long)seg.ObjectRange.Length);
                break;
            case GCSegmentKind.Generation2:
                yield return ("Gen2", (long)seg.ObjectRange.Length);
                break;
            case GCSegmentKind.Ephemeral:
                yield return ("Gen0", (long)seg.Generation0.Length);
                yield return ("Gen1", (long)seg.Generation1.Length);
                yield return ("Gen2", (long)seg.Generation2.Length);
                break;
        }
    }

    private static IReadOnlyList<GenerationSize> OrderGenerations(Dictionary<string, long> gens) =>
        new[] { "Gen0", "Gen1", "Gen2", "LOH", "POH", "Frozen" }
            .Where(gens.ContainsKey)
            .Select(g => new GenerationSize(g, gens[g]))
            .ToList();
}
