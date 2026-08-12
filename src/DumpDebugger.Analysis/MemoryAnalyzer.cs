using DumpDebugger.Core.Dump;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis;

/// <summary>
/// Phase 3 (PLAN.md §3.2/§4.3): type statistics (`!dumpheap -stat` equivalent) and large
/// object identification — "don't just report `byte[] — 84 MB`", read the object's own
/// identifying fields (any string field, anything named Id/Name/Key/Path/Url).
///
/// Walks the heap directly per request rather than building a persistent index; §11.1 flags
/// index storage (SQLite vs. columnar) as an open question to benchmark once real multi-GB
/// dumps are available, not something to guess at here.
/// </summary>
public static class MemoryAnalyzer
{
    private const int LargeObjectThresholdBytes = 85_000; // the LOH threshold
    private const int MaxLargeObjects = 100;
    private const int MaxStringPreviewLength = 120;

    private static readonly string[] IdentifyingFieldNameHints =
        ["id", "name", "key", "path", "url", "guid", "timestamp"];

    public static IReadOnlyList<TypeStat> GetTypeStats(ClrRuntime runtime)
    {
        var byType = new Dictionary<string, (int Count, ulong TotalSize)>();

        foreach (var obj in runtime.Heap.EnumerateObjects())
        {
            if (obj.IsFree || obj.Type is null)
            {
                continue;
            }

            var name = obj.Type.Name ?? "<unknown type>";
            byType.TryGetValue(name, out var agg);
            byType[name] = (agg.Count + 1, agg.TotalSize + obj.Size);
        }

        return byType
            .Select(kvp => new TypeStat(kvp.Key, kvp.Value.Count, kvp.Value.TotalSize))
            .OrderByDescending(t => t.TotalSize)
            .ToList();
    }

    public static IReadOnlyList<LargeObjectInfo> GetLargeObjects(ClrRuntime runtime)
    {
        return runtime.Heap.EnumerateObjects()
            .Where(obj => !obj.IsFree && obj.Type is not null && obj.Size >= LargeObjectThresholdBytes)
            .OrderByDescending(obj => obj.Size)
            .Take(MaxLargeObjects)
            .Select(obj => new LargeObjectInfo(
                obj.Address,
                obj.Type!.Name ?? "<unknown type>",
                obj.Size,
                ReadIdentifyingFields(obj)))
            .ToList();
    }

    private static IReadOnlyList<ObjectFieldPreview> ReadIdentifyingFields(ClrObject obj)
    {
        var previews = new List<ObjectFieldPreview>();
        if (obj.Type is null)
        {
            return previews;
        }

        foreach (var field in obj.Type.Fields)
        {
            try
            {
                if (field.ElementType == ClrElementType.String)
                {
                    var value = field.ReadString(obj.Address, false);
                    if (!string.IsNullOrEmpty(value))
                    {
                        previews.Add(new ObjectFieldPreview(field.Name ?? "<field>", Truncate(value)));
                    }

                    continue;
                }

                var looksIdentifying = field.Name is not null && IdentifyingFieldNameHints.Any(hint =>
                    field.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
                if (!looksIdentifying || !field.IsPrimitive)
                {
                    continue;
                }

                var text = ReadPrimitiveAsString(field, obj.Address);
                if (text is not null)
                {
                    previews.Add(new ObjectFieldPreview(field.Name ?? "<field>", text));
                }
            }
            catch
            {
                // Corrupt/unreadable field data shouldn't take down the whole listing.
            }
        }

        return previews;
    }

    private static string? ReadPrimitiveAsString(ClrInstanceField field, ulong address) => field.ElementType switch
    {
        ClrElementType.Int32 => field.Read<int>(address, false).ToString(),
        ClrElementType.UInt32 => field.Read<uint>(address, false).ToString(),
        ClrElementType.Int64 => field.Read<long>(address, false).ToString(),
        ClrElementType.UInt64 => field.Read<ulong>(address, false).ToString(),
        ClrElementType.Boolean => field.Read<bool>(address, false).ToString(),
        ClrElementType.Double => field.Read<double>(address, false).ToString("G"),
        ClrElementType.Float => field.Read<float>(address, false).ToString("G"),
        _ => null,
    };

    private static string Truncate(string value) =>
        value.Length <= MaxStringPreviewLength ? value : value[..MaxStringPreviewLength] + "…";
}
