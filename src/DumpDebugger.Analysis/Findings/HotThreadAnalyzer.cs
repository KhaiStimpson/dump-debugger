using System.Globalization;
using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;
using Microsoft.Diagnostics.Runtime;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>What a running thread is doing in the framework frames above its application code.</summary>
internal enum HotPattern
{
    None,
    Regex,
    Dictionary,
    Spin,
}

/// <summary>
/// Runaway CPU. A dump is one instant, but the operating system also records how much CPU
/// each thread has used since it started: a thread that is running application code now and
/// has spent most of its life on the CPU is where the CPU goes. The framework frames on top
/// name the usual culprits: a regular expression that backtracks without end, a dictionary
/// corrupted by concurrent writes, a spin-wait.
/// </summary>
public sealed class HotThreadAnalyzer : IFindingAnalyzer
{
    private static readonly TimeSpan MinCpu = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongCpu = TimeSpan.FromSeconds(60);
    private const double MinCpuShare = 0.5;
    private const int ScannedFrames = 12;

    public string Name => "hot-threads";

    public AnalyzerStage Stage => AnalyzerStage.Threads;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        // The header's capture time is in whole seconds; a thread created after it proves the dump is later.
        var times = context.ThreadTimes;
        var created = times.Values.Where(t => t.Created is not null).Select(t => t.Created!.Value).ToList();
        var capturedAt = context.Metadata.CapturedAt is { } c && created.Count > 0 ? (DateTimeOffset?)(created.Max() > c ? created.Max() : c) : context.Metadata.CapturedAt;

        var running = new List<Candidate>();
        foreach (var t in context.Threads)
        {
            if (t.State != ThreadState.Running || t.Wait != WaitKind.None || t.Role is ThreadRole.Gc or ThreadRole.Finalizer || !t.Methods.Any(m => m is not null))
            {
                continue;
            }

            var cpu = TimeSpan.Zero;
            var lifetime = TimeSpan.Zero;
            if (times.TryGetValue(t.OsId, out var time))
            {
                cpu = time.Cpu;
                if (capturedAt is { } at && time.Created is { } start && at > start)
                {
                    lifetime = at - start;
                }
            }

            // Without a creation time the share is unknown; the CPU total alone has to do.
            var share = lifetime > TimeSpan.Zero ? Math.Min(1, cpu / lifetime) : cpu > TimeSpan.Zero ? 1 : 0;
            running.Add(new Candidate(t, PatternOf(t), cpu, lifetime, cpu >= MinCpu && share >= MinCpuShare));
        }

        var groups = running.GroupBy(x => (x.Pattern, Where: x.Thread.WhereName ?? x.Thread.Where ?? "?"))
            .Select(g => (g.Key.Pattern, g.Key.Where, Threads: g.OrderByDescending(x => x.Cpu).ToList()))
            // One thread caught inside a regex or a dictionary proves nothing. Several at the same
            // place at the same instant, or one that has done little else, does.
            .Where(g => g.Threads.Any(x => x.Hot) || (g.Pattern is HotPattern.Regex or HotPattern.Spin && g.Threads.Count >= 2))
            .OrderByDescending(g => g.Threads.Sum(x => x.Cpu.TotalSeconds))
            .Take(4);

        foreach (var (pattern, where, threads) in groups)
        {
            yield return Describe(context, pattern, where, threads, times.Count > 0);
        }
    }

    private AnalyzerResult Describe(AnalysisContext context, HotPattern pattern, string where, List<Candidate> threads, bool haveTimes)
    {
        var n = threads.Count;
        var first = threads[0].Thread;
        var hot = threads.Any(x => x.Hot);
        var cpu = TimeSpan.FromTicks(threads.Sum(x => x.Cpu.Ticks));
        var lifetime = TimeSpan.FromTicks(threads.Sum(x => x.Lifetime.Ticks));
        var used = !haveTimes || cpu < TimeSpan.FromMilliseconds(100)
            ? null
            : $"{Cpu(cpu)} of CPU used" + (n > 1 ? " between them" : "")
              + (lifetime > TimeSpan.Zero ? $" ({Percent(Math.Min(1, cpu / lifetime))} of {(n > 1 ? "their lifetimes" : "its lifetime")})" : "");
        var request = threads.Select(x => x.Thread.Request).FirstOrDefault(r => r is not null);
        var evidence = new List<EntityRef>();
        if (request is not null)
        {
            evidence.Add(EntityUri.RequestRef(request, Plural(n, "thread")));
        }

        evidence.AddRange(threads.Take(4).Select(x => context.ThreadRef(x.Thread)));
        var are = n == 1 ? "is" : "are";
        var magnitude = Math.Min(1, (cpu.TotalSeconds / 600) + (n / 50.0));
        var chip = Chip(EntityUri.Thread(first.Id));

        switch (pattern)
        {
            case HotPattern.Regex:
            {
                var (regex, input) = RegexOnStack(context.Runtime, first);
                var confidence = hot ? Confidence.High : Confidence.Medium;
                return new AnalyzerResult(new Finding(
                    $"cpu.regex.{where}", Name, Severity.Warning, confidence,
                    $"{Plural(n, "thread")} {are} burning CPU in a regular expression called from {Code(where)}",
                    Join(regex is null ? null : $"Pattern {Code(DuplicateStringsAnalyzer.Clip(regex, 120))}", input is null ? null : $"matching \"{DuplicateStringsAnalyzer.Clip(input, 60)}\"", used),
                    "A regular expression with a quantifier inside a quantifier (like `(a+)+`), or alternatives that overlap, can take exponential time on input that almost matches: before it gives up, the engine tries every way of dividing the text between the repeats. This is catastrophic backtracking. The thread stays at 100% CPU for minutes or hours, and input a user controls can trigger it on purpose.",
                    [
                        "Give the regex a timeout so bad input fails instead of spinning: `new Regex(pattern, options, TimeSpan.FromSeconds(1))`, or set the `REGEX_DEFAULT_MATCH_TIMEOUT` AppContext value for the whole app.",
                        "Rewrite the pattern so repeats don't nest or overlap, or use `RegexOptions.NonBacktracking` (.NET 7 and later), which always runs in linear time.",
                        input is null
                            ? $"Open {chip}: the text being matched is among the objects on its stack."
                            : $"Open {chip} to see the whole input. Text that matches right up to its last character is the worst case.",
                    ],
                    [Symptom.SlowCpu, Symptom.Hang],
                    evidence,
                    Scores.For(Severity.Warning, confidence, magnitude)),
                    VerdictHeadline: $"{Sentence(Plural(n, "thread"))} {are} stuck at full CPU in a regular expression called from {where}.",
                    VerdictBody: (regex is null ? "" : $"The pattern {Code(DuplicateStringsAnalyzer.Clip(regex, 120))} {(hot ? "is" : "looks like it is")} backtracking catastrophically on its input. ")
                                 + "Until the match gives up, each thread keeps a core busy and whatever it was serving never finishes.");
            }

            case HotPattern.Dictionary:
            {
                var legacy = context.Metadata.PrimaryRuntime()?.Family == RuntimeFamily.NetFramework;
                return new AnalyzerResult(new Finding(
                    $"cpu.dictionary.{where}", Name, Severity.Warning, Confidence.Medium,
                    $"{Plural(n, "thread")} {are} burning CPU inside a `Dictionary` or `HashSet` used by {Code(where)}",
                    Join($"In {Code(TopFrame(first))}", used),
                    "`Dictionary` and `HashSet` aren't thread-safe. When two threads write to the same instance at once, its internal chains can end up pointing back at themselves, and every later lookup that lands on such a chain goes round forever at 100% CPU. "
                    + (legacy
                        ? ".NET Framework never notices: the thread spins until the process is restarted."
                        : "Modern .NET detects this and throws `InvalidOperationException` instead, so here it is more likely a huge collection, or keys whose `GetHashCode` collides so lookups walk a very long chain."),
                    [
                        $"Find every place the collection in {Code(where)} is written. If more than one thread can get there, guard it with a `lock` or switch to `ConcurrentDictionary`.",
                        legacy ? "Restart the process to recover now: a corrupted dictionary doesn't heal." : "Check the key type's `GetHashCode` and `Equals`: a constant or poorly spread hash turns every lookup into a scan.",
                        $"Open {chip}: the collection is among the objects on its stack, so its size and owner can be read there.",
                    ],
                    [Symptom.SlowCpu, Symptom.Hang],
                    evidence,
                    Scores.For(Severity.Warning, Confidence.Medium, magnitude)));
            }

            case HotPattern.Spin:
            {
                var confidence = hot ? Confidence.High : Confidence.Medium;
                return new AnalyzerResult(new Finding(
                    $"cpu.spin.{where}", Name, Severity.Warning, confidence,
                    $"{Plural(n, "thread")} {are} spin-waiting in {Code(where)}",
                    Join($"In {Code(TopFrame(first))}", used),
                    "A spin-wait keeps the CPU busy while it checks a condition over and over. That is cheaper than blocking when the wait lasts nanoseconds. When the condition takes long, or never comes, each spinning thread burns a whole core doing nothing.",
                    [
                        $"Find what {Code(where)} is waiting for and why it hasn't happened.",
                        "Replace the spin with a wait that sleeps: `SemaphoreSlim`, `ManualResetEventSlim`, or a `TaskCompletionSource` the other side completes.",
                        "If the spin is a `SpinLock`, never hold it across anything slow (I/O, allocation, another lock).",
                    ],
                    [Symptom.SlowCpu, Symptom.Hang],
                    evidence,
                    Scores.For(Severity.Warning, confidence, magnitude)));
            }

            default:
            {
                var severity = threads[0].Cpu >= LongCpu ? Severity.Warning : Severity.Info;
                return new AnalyzerResult(new Finding(
                    $"cpu.busy.{where}", Name, severity, Confidence.Medium,
                    $"{Plural(n, "thread")} {(n == 1 ? "has" : "have")} been running {Code(where)} flat out",
                    Join(used, "still running when the dump was taken"),
                    "The operating system records how much CPU each thread has used since it started. " + (n == 1 ? "This thread has" : "These threads have")
                    + " spent most of that time executing rather than waiting, and " + (n == 1 ? "was" : "were") + " still running application code at the moment of the dump: this is where the CPU goes. It can be legitimate work, such as a batch job, or a loop that never ends.",
                    [
                        $"Open {chip}: the top frame is where it was at that instant, and the loop is in one of the first application frames.",
                        "Take a second dump a few seconds later. The same method with more CPU used means the loop isn't finishing.",
                        "If the work is legitimate but starves everything else, limit how many threads do it at once or run it at a lower priority.",
                    ],
                    [Symptom.SlowCpu],
                    evidence,
                    Scores.For(severity, Confidence.Medium, magnitude)));
            }
        }
    }

    /// <summary>The pattern named by the framework frames above the thread's first application frame.</summary>
    private static HotPattern PatternOf(ThreadRecord t)
    {
        var app = t.FirstAppFrameIndex;
        return Classify(t.Frames.Take(app >= 0 ? Math.Min(app, ScannedFrames) : ScannedFrames).Select(f => f.FullName));
    }

    internal static HotPattern Classify(IEnumerable<string?> frames)
    {
        foreach (var frame in frames)
        {
            if (frame is null)
            {
                continue;
            }

            if (frame.StartsWith("System.Text.RegularExpressions.", StringComparison.Ordinal))
            {
                return HotPattern.Regex;
            }

            if (frame.StartsWith("System.Collections.Generic.Dictionary", StringComparison.Ordinal)
                || frame.StartsWith("System.Collections.Generic.HashSet", StringComparison.Ordinal)
                || frame.StartsWith("System.Collections.Hashtable.", StringComparison.Ordinal))
            {
                return HotPattern.Dictionary;
            }

            if (frame.StartsWith("System.Threading.SpinWait.", StringComparison.Ordinal)
                || frame.StartsWith("System.Threading.SpinLock.", StringComparison.Ordinal)
                || frame == "System.Threading.Thread.SpinWait")
            {
                return HotPattern.Spin;
            }
        }

        return HotPattern.None;
    }

    /// <summary>The pattern of the Regex on a thread's stack and the text it is matching, when they can be read.</summary>
    private static (string? Pattern, string? Input) RegexOnStack(ClrRuntime runtime, ThreadRecord t)
    {
        string? pattern = null;
        string? input = null;
        try
        {
            var app = t.FirstAppFrameIndex;
            foreach (var root in t.Roots.Where(r => r.FrameIndex >= 0 && (app < 0 || r.FrameIndex < app)))
            {
                var obj = runtime.Heap.GetObject(root.Address);
                if (input is null && root.TypeName == "System.String")
                {
                    input = obj.AsString(200);
                }
                else if (pattern is null && IsRegex(obj.Type))
                {
                    pattern = obj.ReadStringField("pattern");
                }
            }
        }
        catch
        {
            // Both are a bonus: the finding stands without them.
        }

        return (pattern, string.IsNullOrEmpty(input) ? null : input);
    }

    private static bool IsRegex(ClrType? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.Name == "System.Text.RegularExpressions.Regex")
            {
                return true;
            }
        }

        return false;
    }

    private static string TopFrame(ThreadRecord t) =>
        t.Frames.FirstOrDefault(f => f.Kind != FrameKind.Runtime)?.Display ?? t.Frames.FirstOrDefault()?.Display ?? "?";

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>"6.2 s", "12 min", "3.4 h".</summary>
    internal static string Cpu(TimeSpan t) => t.TotalSeconds < 60
        ? $"{t.TotalSeconds.ToString(t.TotalSeconds < 10 ? "F1" : "F0", CultureInfo.InvariantCulture)} s"
        : t.TotalMinutes < 60
            ? $"{t.TotalMinutes.ToString(t.TotalMinutes < 10 ? "F1" : "F0", CultureInfo.InvariantCulture)} min"
            : $"{t.TotalHours.ToString(t.TotalHours < 10 ? "F1" : "F0", CultureInfo.InvariantCulture)} h";

    private sealed record Candidate(ThreadRecord Thread, HotPattern Pattern, TimeSpan Cpu, TimeSpan Lifetime, bool Hot);
}
