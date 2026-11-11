namespace DumpDebugger.Core.Contracts;

/// <summary><c>!dumpheap -stat</c> row: one type's live instance count and total size.</summary>
public sealed record TypeStat(string TypeName, long Count, long TotalSize);

public sealed record GenerationSize(string Name, long Bytes);

/// <summary>Heap totals, computed from segment ranges (cheap) and refined by the heap walk.</summary>
public sealed record HeapOverview(
    long TotalBytes,
    long ObjectCount,
    int TypeCount,
    IReadOnlyList<GenerationSize> Generations,
    bool Walked);
