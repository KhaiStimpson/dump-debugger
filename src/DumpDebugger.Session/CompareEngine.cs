using System.Globalization;
using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Session;

/// <summary>
/// Compares two cached <see cref="HeapSnapshot"/>s. Pure: no worker, no dump access, so a
/// baseline taken last week compares as fast as one taken a minute ago.
/// </summary>
public static class CompareEngine
{
    private const long MinCountDelta = 1_000;
    private const double MinGrowth = 3.0;
    private const double MinRootShare = 0.6;

    public static CompareResult Compare(HeapSnapshot a, HeapSnapshot b, bool bIsOpen, bool aIsOpen = false)
    {
        var typesA = a.Types.ToDictionary(t => t.TypeName);
        var typesB = b.Types.ToDictionary(t => t.TypeName);
        var deltas = typesA.Keys.Union(typesB.Keys)
            .Select(name =>
            {
                var ta = typesA.GetValueOrDefault(name);
                var tb = typesB.GetValueOrDefault(name);
                var countA = ta?.Count ?? 0;
                var countB = tb?.Count ?? 0;
                return new TypeDelta(name, countA, countB, ta?.TotalSize ?? 0, tb?.TotalSize ?? 0,
                    (tb?.TotalSize ?? 0) - (ta?.TotalSize ?? 0), countB - countA,
                    countA > 0 ? (double)countB / countA : null);
            })
            .Where(d => d.SizeDelta != 0 || d.CountDelta != 0 || d.SizeB > 1024 * 1024)
            .OrderByDescending(d => Math.Abs(d.SizeDelta))
            .Take(300)
            .ToList();

        var groupsA = a.ThreadGroups.GroupBy(Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        var groupsB = b.ThreadGroups.GroupBy(Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        var threadDeltas = groupsA.Keys.Union(groupsB.Keys)
            .Select(k => new ThreadGroupDelta(k.Where, k.Request, k.StateLabel, groupsA.GetValueOrDefault(k), groupsB.GetValueOrDefault(k)))
            .Where(d => d.CountA != d.CountB)
            .OrderByDescending(d => Math.Abs(d.CountB - d.CountA))
            .Take(40)
            .ToList();

        var findingsA = a.Findings.ToDictionary(f => f.Id);
        var findingsB = b.Findings.ToDictionary(f => f.Id);
        var findingDiffs = findingsB.Values.Where(f => !findingsA.ContainsKey(f.Id)).Select(f => new FindingDiff(FindingChange.New, f.Severity, f.Title, f.Analyzer))
            .Concat(findingsA.Values.Where(f => !findingsB.ContainsKey(f.Id)).Select(f => new FindingDiff(FindingChange.Resolved, f.Severity, f.Title, f.Analyzer)))
            .Concat(findingsB.Values.Where(f => findingsA.ContainsKey(f.Id)).Select(f => new FindingDiff(FindingChange.Persisting, f.Severity, f.Title, f.Analyzer)))
            .OrderBy(f => f.Change).ThenByDescending(f => f.Severity)
            .ToList();

        var suspects = LeakSuspects(deltas, b);
        var elapsed = a.CapturedAt is { } ca && b.CapturedAt is { } cb ? cb - ca : (TimeSpan?)null;
        var heapDelta = b.TotalBytes - a.TotalBytes;
        var objectDelta = b.ObjectCount - a.ObjectCount;
        var topObjects = deltas.Where(d => d.CountDelta > 0 && !IsInfrastructure(d.TypeName)).OrderByDescending(d => d.CountDelta).FirstOrDefault();
        var newFindings = findingDiffs.Where(f => f.Change == FindingChange.New).ToList();
        var resolved = findingDiffs.Count(f => f.Change == FindingChange.Resolved);

        var summary = new List<Vital>
        {
            new("heap", "Heap", Signed(heapDelta, Bytes), a.TotalBytes > 0 ? $"{Pct((double)heapDelta / a.TotalBytes)}" + (elapsed is { } e ? $" in {Span(e)}" : "") : "", null, heapDelta > 0 ? Tone.Critical : heapDelta < 0 ? Tone.Ok : Tone.Neutral),
            new("objects", "Objects", Signed(objectDelta, Count), topObjects is null || objectDelta <= 0 ? "" : $"{Short(topObjects.TypeName)} accounts for {Pct((double)topObjects.CountDelta / objectDelta, signed: false)}", null, objectDelta > 0 ? Tone.Critical : Tone.Neutral),
            new("blocked", "Blocked threads", $"{a.BlockedThreads} → {b.BlockedThreads}", b.BlockedThreads > a.BlockedThreads && newFindings.Any(f => f.Analyzer == "deadlock") ? "new deadlock" : $"{a.ThreadCount} → {b.ThreadCount} threads", null, b.BlockedThreads > a.BlockedThreads ? Tone.Critical : Tone.Neutral),
            new("findings", "Findings", $"+{newFindings.Count} / −{resolved}", newFindings.Count == 0 ? "nothing new" : "new: " + string.Join(", ", newFindings.Take(2).Select(f => Badge(f))), null, newFindings.Any(f => f.Severity >= Severity.Warning) ? Tone.Warning : Tone.Neutral),
        };

        return new CompareResult(Side(a, aIsOpen), Side(b, bIsOpen), summary, deltas, threadDeltas, findingDiffs, suspects);
    }

    private static IReadOnlyList<LeakSuspect> LeakSuspects(IReadOnlyList<TypeDelta> deltas, HeapSnapshot b)
    {
        var samples = b.RootSamples.ToDictionary(s => s.TypeName);
        var candidates = new List<(TypeDelta Delta, TypeRootSample? Sample, RootShare? Root, double? Share, bool Rooted)>();
        foreach (var d in deltas.Where(d => d.CountDelta >= MinCountDelta && (d.Growth is null || d.Growth >= MinGrowth)).OrderByDescending(d => d.CountDelta))
        {
            if (IsInfrastructure(d.TypeName))
            {
                continue;
            }

            var sample = samples.GetValueOrDefault(d.TypeName);
            var top = sample?.Roots.FirstOrDefault();
            double? share = top is not null && sample!.Sampled > 0 ? (double)top.Count / sample.Sampled : null;
            candidates.Add((d, sample, top, share, top is not null && top.Label != "unrooted" && share >= MinRootShare));
        }

        // Several types usually grow together under one root (snapshots and the lines inside
        // them). Report the one closest to the root, the holder, and mention the rest.
        var result = new List<LeakSuspect>();
        foreach (var group in candidates.Where(c => c.Rooted).GroupBy(c => c.Root!.Label))
        {
            var ordered = group.OrderBy(c => c.Sample!.AverageDepth).ThenBy(c => c.Delta.CountB).ToList();
            result.Add(Suspect(ordered[0], ordered.Skip(1).Select(c => c.Delta).ToList()));
        }

        result.AddRange(candidates.Where(c => !c.Rooted).Take(Math.Max(0, 5 - result.Count)).Select(c => Suspect(c, [])));

        // Rooted suspects first: they come with a retention path to follow.
        return result.OrderByDescending(s => s.DominantRoot is not null).ThenByDescending(s => s.CountB - s.CountA).Take(5).ToList();
    }

    private static LeakSuspect Suspect((TypeDelta Delta, TypeRootSample? Sample, RootShare? Root, double? Share, bool Rooted) c, IReadOnlyList<TypeDelta> alsoGrew)
    {
        var d = c.Delta;
        var growth = d.Growth is { } g ? $"grew {g.ToString(g >= 10 ? "F0" : "F1", CultureInfo.InvariantCulture)}× ({Count(d.CountA)} → {Count(d.CountB)})" : $"appeared ({Count(d.CountB)} instances)";
        var explanation = $"`{Short(d.TypeName)}` {growth}, +{Bytes(d.SizeDelta)}."
                          + (c.Rooted ? $" {Pct(c.Share!.Value, signed: false)} of sampled instances in B are kept alive by `{c.Root!.Label}`." : "")
                          + (c.Rooted && c.Root!.Kind == RootKind.Static ? " A static collection that only grows is the classic managed leak." : "")
                          + (alsoGrew.Count > 0 ? $" What it holds grew with it: {string.Join(", ", alsoGrew.Take(3).Select(x => $"`{Short(x.TypeName)}` +{Count(x.CountDelta)}"))}." : "");
        return new LeakSuspect(d.TypeName, d.CountA, d.CountB, d.SizeDelta, d.Growth, c.Rooted ? c.Root!.Label : null, c.Share, c.Sample?.SampleAddresses ?? [], explanation);
    }

    /// <summary>Strings and arrays grow along with whatever holds them; report the holder, not the payload.</summary>
    private static bool IsInfrastructure(string type) =>
        type is "System.String" or "System.Object" or "Free" || type.EndsWith("[]", StringComparison.Ordinal)
        || type.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal);

    private static (string Where, string? Request, string StateLabel) Key(SnapshotThreadGroup g) => (g.Where, g.Request, g.StateLabel);

    private static CompareSide Side(HeapSnapshot s, bool isOpen) => new(
        s.Dump.Path, s.Dump.FileName, s.CapturedAt, s.TotalBytes, s.ObjectCount, s.ThreadCount, s.BlockedThreads,
        s.Findings.Count(f => f.Severity == Severity.Critical), s.Dump.Runtime, isOpen);

    private static string Badge(FindingDiff f) => f.Analyzer switch
    {
        "deadlock" => "deadlock",
        "threadpool-starvation" => "starvation",
        "heap-dominance" => "heap",
        _ => f.Analyzer.Replace('-', ' '),
    };

    private static string Short(string type)
    {
        var lt = type.IndexOf('<');
        var head = lt >= 0 ? type[..lt] : type;
        var dot = head.LastIndexOf('.');
        return dot >= 0 ? type[(dot + 1)..] : type;
    }

    private static string Signed(long v, Func<long, string> format) => v > 0 ? "+" + format(v) : v < 0 ? "−" + format(-v) : "±0";

    private static string Pct(double v, bool signed = true)
    {
        var s = Math.Round(Math.Abs(v) * 100).ToString(CultureInfo.InvariantCulture) + "%";
        return signed ? (v >= 0 ? "+" : "−") + s : s;
    }

    private static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private static string Span(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)Math.Max(1, t.TotalMinutes)}m";

    private static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{bytes} B" : $"{v.ToString(v >= 100 ? "F0" : v >= 10 ? "F1" : "F2", CultureInfo.InvariantCulture)} {units[u]}";
    }
}
