namespace DumpDebugger.Core.Dump;

/// <summary>`!dumpheap -stat` equivalent row (PLAN.md §3.2 "Type statistics").</summary>
public sealed record TypeStat(
    string TypeName,
    int Count,
    ulong TotalSize);

/// <summary>One identifying field read off a large object (PLAN.md §4.3 "object identification").</summary>
public sealed record ObjectFieldPreview(string FieldName, string Value);

public sealed record LargeObjectInfo(
    ulong Address,
    string TypeName,
    ulong Size,
    IReadOnlyList<ObjectFieldPreview> IdentifyingFields);
