using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Analysis.Heap;

/// <summary>
/// Builds the compact <see cref="HeapSnapshot"/> Compare works from. Root sampling is the only
/// expensive part: for the biggest types (by size and by count) a spread of instances gets a
/// shortest root path each, and the root labels are tallied.
/// </summary>
public static class SnapshotBuilder
{
    private const int TypesToSample = 40;
    private const int SamplesPerType = 12;

    public static HeapSnapshot Build(DumpIndex index, Diagnosis diagnosis, ObjectExplorer explorer, CancellationToken ct)
    {
        var heap = index.Heap ?? throw new InvalidOperationException("Walk the heap before building a snapshot.");
        var threads = index.Threads ?? [];
        var groups = ThreadQueries.Groups(threads, new ThreadGroupsQuery(ThreadGrouping.Stack, ThreadStateFilter.All, null)).Groups
            .Select(g => new SnapshotThreadGroup(g.Where, g.Request, g.StateLabel, g.Count))
            .ToList();

        var sampleTypes = heap.Types.OrderByDescending(t => t.Size).Take(TypesToSample)
            .Concat(heap.Types.OrderByDescending(t => t.Count).Take(TypesToSample))
            .Select(t => t.Name)
            .Distinct()
            .ToHashSet();

        var instances = new Dictionary<string, List<int>>();
        var ix = explorer.Index;
        for (var i = 0; i < ix.Count; i++)
        {
            var name = ix.TypeOf(i).Name;
            if (name is not null && sampleTypes.Contains(name))
            {
                (instances.TryGetValue(name, out var list) ? list : instances[name] = []).Add(i);
            }
        }

        var samples = new List<TypeRootSample>();
        foreach (var (name, all) in instances)
        {
            ct.ThrowIfCancellationRequested();
            var step = Math.Max(1, all.Count / SamplesPerType);
            var picked = all.Where((_, k) => k % step == 0).Take(SamplesPerType).ToList();
            var tally = new Dictionary<(string, RootKind), int>();
            var depths = new List<int>();
            foreach (var i in picked)
            {
                var path = explorer.FindRoots(new RootsQuery(ix.Addresses[i], RootPathMode.Shortest), ct).Paths.FirstOrDefault();
                var root = path?.Root;
                if (path is not null)
                {
                    depths.Add(path.Steps.Count);
                }

                var key = root is null ? ("unrooted", RootKind.Other) : (root.Label, root.Kind);
                tally[key] = tally.GetValueOrDefault(key) + 1;
            }

            samples.Add(new TypeRootSample(
                name,
                picked.Count,
                tally.OrderByDescending(kv => kv.Value).Select(kv => new RootShare(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList(),
                picked.Take(5).Select(i => ix.Addresses[i]).ToList(),
                depths.Count == 0 ? 0 : depths.Average()));
        }

        return new HeapSnapshot(
            HeapSnapshot.CurrentSchemaVersion,
            diagnosis.Dump,
            index.Loaded.Metadata.CapturedAt,
            heap.Overview.TotalBytes,
            heap.Overview.ObjectCount,
            threads.Count,
            threads.Count(t => t.State == ThreadState.Blocked),
            heap.TypeStats.ToList(),
            groups,
            diagnosis.Findings.Select(f => new SnapshotFinding(f.Id, f.Analyzer, f.Severity, f.Title)).ToList(),
            samples.OrderBy(s => s.TypeName, StringComparer.Ordinal).ToList());
    }
}
