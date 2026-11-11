using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Analysis.Heap;

/// <summary>Type table, namespace/generation buckets and treemap cells, answered from the heap walk's aggregates.</summary>
public static class HeapQueries
{
    private const int TreemapCells = 8;

    public static HeapSummary Summary(HeapModel heap)
    {
        var total = Math.Max(1, heap.Overview.TotalBytes);
        var top = heap.Types.Take(TreemapCells).ToList();
        var cells = top.Select(t => new TreemapCell(t.Name, Findings.Text.Bytes(t.Size), t.Size, (double)t.Size / total, false, 0)).ToList();
        var rest = heap.Types.Skip(TreemapCells).ToList();
        if (rest.Count > 0)
        {
            var size = rest.Sum(t => t.Size);
            cells.Add(new TreemapCell($"other ({rest.Count:N0})", Findings.Text.Bytes(size), size, (double)size / total, true, rest.Count));
        }

        return new HeapSummary(heap.Overview, cells, heap.FreeBytes, heap.LohFreeBytes);
    }

    public static Page<TypeRow> Types(HeapModel heap, HeapTypesQuery q)
    {
        var total = Math.Max(1, heap.Overview.TotalBytes);
        IEnumerable<TypeRow> rows = q.GroupBy switch
        {
            HeapGrouping.Namespace => heap.Types
                .GroupBy(t => Namespace(t.Name))
                .Select(g => new TypeRow(g.Key, g.Sum(t => t.Count), g.Sum(t => t.Size), g.Sum(t => t.LohSize), (double)g.Sum(t => t.Size) / total, g.Any(t => t.IsApp))),
            HeapGrouping.Generation => heap.GenerationStats
                .Select(kv => new TypeRow(kv.Key, kv.Value.Count, kv.Value.Size, kv.Key == "LOH" ? kv.Value.Size : 0, (double)kv.Value.Size / total, false)),
            _ => heap.Types.Select(t => new TypeRow(t.Name, t.Count, t.Size, t.LohSize, (double)t.Size / total, t.IsApp)),
        };

        if (!string.IsNullOrWhiteSpace(q.Filter))
        {
            var f = q.Filter.Trim();
            rows = rows.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        rows = q.Sort switch
        {
            HeapSort.Count => rows.OrderByDescending(r => r.Count),
            HeapSort.Name => rows.OrderBy(r => r.Name, StringComparer.Ordinal),
            _ => rows.OrderByDescending(r => r.Size),
        };

        var list = rows.ToList();
        return new Page<TypeRow>(list.Skip(q.Offset).Take(Math.Clamp(q.Limit, 1, 1000)).ToList(), list.Count, q.Offset);
    }

    /// <summary>System.Collections.Generic.List&lt;Contoso.X&gt; → System.Collections.Generic</summary>
    public static string Namespace(string typeName)
    {
        var lt = typeName.IndexOf('<');
        var head = lt >= 0 ? typeName[..lt] : typeName;
        var plus = head.IndexOf('+');
        if (plus >= 0)
        {
            head = head[..plus];
        }

        var dot = head.LastIndexOf('.');
        return dot > 0 ? head[..dot] : "(global)";
    }
}
