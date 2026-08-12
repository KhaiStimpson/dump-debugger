using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Findings;
using DumpDebugger.Core.Source;

namespace DumpDebugger.Analysis;

/// <summary>
/// Ranks application-code frames (framework frames excluded — see FrameworkFrames; otherwise
/// every dump would report ThreadPoolWorkQueue.Dispatch as a "hot path" shared by every worker
/// thread, which is true and useless) by how many distinct threads have them on their stack.
/// A frame shared by a large fraction of threads is either the app's normal entry point or a
/// genuine contention point — either way, it's the choke point in the actual codebase worth
/// looking at first, and each finding carries the resolved SourceLocation (when Source Link
/// found one) so Triage can link straight to it.
/// </summary>
public static class HotPathAnalyzer
{
    private const int MinThreadCount = 3;
    private const double MinFraction = 0.1;
    private const int MaxFindings = 5;
    private const int MaxSampleThreads = 5;

    public static IReadOnlyList<Finding> Analyze(IReadOnlyList<ThreadInfo> threads)
    {
        if (threads.Count == 0)
        {
            return [];
        }

        var byKey = new Dictionary<string, Accumulator>();

        foreach (var thread in threads)
        {
            var seenInThisThread = new HashSet<string>();
            foreach (var frame in thread.Frames)
            {
                if (frame.TypeName is null || FrameworkFrames.IsFrameworkType(frame.TypeName))
                {
                    continue;
                }

                var key = frame.Location is { } loc
                    ? $"{loc.RepoUrl}|{loc.RelativePath}:{loc.Line}"
                    : $"{frame.TypeName}.{frame.MethodName}";

                if (!seenInThisThread.Add(key))
                {
                    continue; // Count each thread once per frame, even if it recurses through it.
                }

                if (!byKey.TryGetValue(key, out var acc))
                {
                    acc = new Accumulator(frame.TypeName, frame.MethodName, frame.Location);
                    byKey[key] = acc;
                }

                acc.ThreadIds.Add(thread.OSThreadId);
            }
        }

        var total = threads.Count;
        return byKey.Values
            .Where(a => a.ThreadIds.Count >= MinThreadCount && (double)a.ThreadIds.Count / total >= MinFraction)
            .OrderByDescending(a => a.ThreadIds.Count)
            .Take(MaxFindings)
            .Select(a => ToFinding(a, total))
            .ToList();
    }

    private static Finding ToFinding(Accumulator acc, int totalThreads)
    {
        var fraction = (double)acc.ThreadIds.Count / totalThreads;
        var locationText = acc.Location is { } loc ? $"{loc.RelativePath}:{loc.Line}" : $"{acc.TypeName}.{acc.MethodName}";

        var evidence = new List<EvidenceItem>
        {
            new(
                "location",
                locationText,
                $"{acc.ThreadIds.Count} of {totalThreads} threads ({fraction:P0}) pass through this frame",
                acc.Location),
        };

        evidence.AddRange(acc.ThreadIds
            .OrderBy(id => id)
            .Take(MaxSampleThreads)
            .Select(id => new EvidenceItem("thread", id.ToString(), "one of the threads on this hot path")));

        var idSlug = locationText.Replace('/', '-').Replace('\\', '-').Replace(':', '-');

        return new Finding(
            Id: $"hotpath.{idSlug}",
            Analyzer: nameof(HotPathAnalyzer),
            Severity: fraction >= 0.5 ? Severity.Warning : Severity.Info,
            Confidence: Confidence.High,
            Title: $"Hot path: {acc.ThreadIds.Count} threads ({fraction:P0}) share {locationText}",
            Summary: $"{acc.ThreadIds.Count} of {totalThreads} threads have `{locationText}` on their stack. " +
                      "A frame shared by a large fraction of threads is either the application's main entry " +
                      "point (expected) or a contention point worth investigating.",
            Evidence: evidence,
            Links: []);
    }

    private sealed class Accumulator(string typeName, string methodName, SourceLocation? location)
    {
        public string TypeName { get; } = typeName;
        public string MethodName { get; } = methodName;
        public SourceLocation? Location { get; } = location;
        public HashSet<int> ThreadIds { get; } = [];
    }
}
