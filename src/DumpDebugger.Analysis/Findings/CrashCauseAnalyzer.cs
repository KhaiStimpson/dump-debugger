using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;
using Microsoft.Diagnostics.Runtime;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// Crashes that aren't an unhandled managed exception (those are the exceptions analyzer's):
/// a stack overflow, named by the methods that recurse; a deliberate
/// <c>Environment.FailFast</c>, with its message; and a native exception such as an access
/// violation. Also flags recursion that is deep but hasn't overflowed yet.
/// </summary>
public sealed class CrashCauseAnalyzer : IFindingAnalyzer
{
    internal const uint StackOverflowCode = 0xC00000FD;
    private const uint ClrExceptionCode = 0xE0434352;
    private const string StackOverflowType = "System.StackOverflowException";

    private const int MinRepeatedFrames = 40;
    private const int MaxPeriod = 16;
    private const long DeepStackBytes = 256 * 1024;
    private const int ScanBytes = 256 * 1024;
    private const int MaxLookups = 20_000;

    public string Name => "crash";

    public AnalyzerStage Stage => AnalyzerStage.Threads;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        var md = context.Metadata;
        var crashThread = md.CrashThreadOsId is { } os ? context.Threads.FirstOrDefault(t => t.OsId == os) : null;
        var explained = false;

        foreach (var t in context.Threads)
        {
            var overflowed = t.Exception == StackOverflowType || (t == crashThread && md.CrashExceptionCode == StackOverflowCode);
            var names = t.Frames.Select((f, i) => i < t.Methods.Count && t.Methods[i] is { } m ? FrameClassifier.DisplayName(m) : f.Display).ToList();
            var loop = LoopOf(names);

            // The runtime can't walk a stack that has overflowed, so the frames are usually
            // missing: read the methods straight from the thread's stack memory instead.
            if (overflowed && loop is null)
            {
                loop = LoopOf(ScanStack(context.Runtime, t, md.Architecture));
            }

            if (overflowed)
            {
                explained = true;
                yield return StackOverflow(context, t, loop);
            }
            else if (loop is not null && t.FramesTruncated && t.StackUsedBytes >= DeepStackBytes)
            {
                yield return DeepRecursion(context, t, loop);
            }
        }

        foreach (var t in context.Threads)
        {
            var failFast = t.Frames.Take(16).ToList().FindLastIndex(f => f.FullName == "System.Environment.FailFast");
            if (failFast >= 0)
            {
                explained = true;
                yield return FailFast(context, t, failFast);
            }
        }

        if (!explained && md is { IsCrashDump: true, CrashExceptionCode: { } code } && code != ClrExceptionCode
            && crashThread?.Exception is null && NativeExceptionName(code) is { } what)
        {
            yield return NativeCrash(context, crashThread, code, what);
        }
    }

    private AnalyzerResult StackOverflow(AnalysisContext context, ThreadRecord t, IReadOnlyList<string>? loop)
    {
        var stack = t.StackCommittedBytes is { } bytes ? $"{Bytes(bytes)} of stack used up" : "its stack used up";
        var title = loop is null ? "Stack overflow: a thread ran out of stack" : $"Stack overflow: {DescribeLoop(loop)}";
        var why = $"Thread #{t.Id} · {stack}"
                  + (loop is null ? "" : loop.Count == 1 ? " · the same frame repeats all the way down" : $" · the same {loop.Count} frames repeat all the way down");
        var headline = loop is null
            ? "The process crashed with a stack overflow."
            : loop.Count == 1
                ? $"The process crashed with a stack overflow: {loop[0]} called itself until the thread ran out of stack."
                : $"The process crashed with a stack overflow: {loop.Count} methods called each other in a loop until the thread ran out of stack.";
        return new AnalyzerResult(new Finding(
            "crash.stack-overflow", Name, Severity.Critical, Confidence.High, title, why,
            "Every call takes stack space, and a thread has a fixed amount (typically 1 to 1.5 MB on Windows, less under IIS). Recursion with no reachable end fills it. A stack overflow can't be caught in .NET: the runtime ends the process on the spot, without running `catch` or `finally` blocks.",
            [
                loop is null
                    ? $"Open {Chip(EntityUri.Thread(t.Id))}. The runtime also prints the overflowing stack to stderr and the Windows event log as it dies."
                    : $"Find why {Code(loop[0])} never reaches its end: a method or property that calls itself by mistake, a base case that is never hit, or a cycle in the data (an item that is its own ancestor).",
                "If the depth is legitimate (a deep tree, a long linked structure), replace the recursion with a loop and an explicit `Stack<T>`.",
                "As a guard, call `RuntimeHelpers.EnsureSufficientExecutionStack()` at the top of the recursive method: it throws a catchable `InsufficientExecutionStackException` instead of taking the process down.",
            ],
            [Symptom.Crash],
            [context.ThreadRef(t)],
            Scores.For(Severity.Critical, Confidence.High, 1)),
            VerdictHeadline: headline,
            VerdictBody: $"It happened on {Chip(EntityUri.Thread(t.Id))}, which had {stack}. A stack overflow can't be caught, so the process ended immediately.");
    }

    private AnalyzerResult DeepRecursion(AnalysisContext context, ThreadRecord t, IReadOnlyList<string> loop) =>
        new(new Finding(
            $"crash.deep-recursion.{loop[0]}", Name, Severity.Warning, Confidence.Medium,
            $"Deep recursion: {DescribeLoop(loop)}",
            $"Thread #{t.Id} · {Bytes(t.StackUsedBytes ?? 0)} of stack in use · more than {t.Frames.Count} frames deep",
            "This thread is hundreds of calls deep in the same methods and has used a large part of its stack (typically 1 to 1.5 MB on Windows). If the recursion goes much further the stack overflows, and a stack overflow can't be caught: it ends the process.",
            [
                $"Check what bounds the depth of {Code(loop[0])}: the size of the input, or nothing (a cycle in the data).",
                "Replace the recursion with a loop and an explicit `Stack<T>` if the input can be arbitrarily deep.",
                "Take a second dump a moment later: a stack that is deeper still means the recursion isn't ending.",
            ],
            [Symptom.Crash, Symptom.SlowCpu],
            [context.ThreadRef(t)],
            Scores.For(Severity.Warning, Confidence.Medium, Math.Min(1, (t.StackUsedBytes ?? 0) / (1024.0 * 1024)))));

    private AnalyzerResult FailFast(AnalysisContext context, ThreadRecord t, int failFastFrame)
    {
        var caller = failFastFrame + 1 < t.Frames.Count
            ? t.Methods[failFastFrame + 1] is { } m ? FrameClassifier.DisplayName(m) : t.Frames[failFastFrame + 1].Display
            : null;
        string? message = null;
        try
        {
            // The message argument is still on the FailFast frames.
            message = t.Roots.Where(r => r.FrameIndex >= 0 && r.FrameIndex <= failFastFrame && r.TypeName == "System.String")
                .Select(r => context.Runtime.Heap.GetObject(r.Address).AsString(400))
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        }
        catch
        {
            // The message is a bonus; the finding stands without it.
        }

        return new AnalyzerResult(new Finding(
            "crash.fail-fast", Name, Severity.Critical, Confidence.High,
            "The process ended itself: `Environment.FailFast`" + (caller is null ? "" : $" called from {Code(caller)}"),
            (message is null ? "" : $"\"{DuplicateStringsAnalyzer.Clip(message, 160)}\" · ") + $"thread #{t.Id}",
            "`Environment.FailFast` stops the process immediately and on purpose: no `finally` blocks, no finalizers, no unhandled-exception handlers. Code calls it when it decides that carrying on would be worse than stopping, such as a broken invariant or corrupted state. This dump was written as the process went down.",
            [
                $"Open {Chip(EntityUri.Thread(t.Id))}: the frames below `FailFast` show what led to the call" + (message is null ? "." : ", and the message says why."),
                caller is null
                    ? "Decide whether the condition should really be fatal. If the caller can recover, throw an exception instead."
                    : $"Decide whether the condition in {Code(caller)} should really be fatal. If the caller can recover, throw an exception instead; if it can't, fix what broke the invariant.",
                "The same message is written to stderr and to the Windows Application event log (source \".NET Runtime\"), so earlier occurrences can be found there.",
            ],
            [Symptom.Crash],
            [context.ThreadRef(t)],
            Scores.For(Severity.Critical, Confidence.High, 1)),
            VerdictHeadline: "The process ended itself with Environment.FailFast" + (caller is null ? "." : $", called from {caller}."),
            VerdictBody: (message is null ? "" : $"\"{DuplicateStringsAnalyzer.Clip(message, 200)}\" ") + $"It was called on {Chip(EntityUri.Thread(t.Id))}; nothing after that point ran.");
    }

    private AnalyzerResult NativeCrash(AnalysisContext context, ThreadRecord? t, uint code, string what)
    {
        var where = t is { WhereIsApp: true, WhereName: { } w } ? w : null;
        var hex = $"0x{code:X8}";
        var meaning = code switch
        {
            0xC0000005 => "The code read or wrote memory it doesn't own. In a .NET process that almost always comes from native or unsafe code: a P/Invoke with the wrong signature, a buffer that was freed or moved while native code still used it, pointer arithmetic in an `unsafe` block, or a bug in a native library. The managed heap may also have been corrupted earlier by one of those, in which case the crash site is a victim rather than the cause.",
            0xC0000374 => "The Windows heap found its own bookkeeping damaged. Something wrote past the end of a native allocation, or freed one twice, some time before this point: the thread that crashed is usually the one that noticed, not the one that did it.",
            StackOverflowCode => "A thread used up its stack, almost always through recursion that never ends. A stack overflow can't be caught: the process ends on the spot.",
            _ => "The operating system raised an exception that nothing handled, so the process was terminated. This isn't a .NET exception: it comes from native code, or from managed code doing something the runtime can't turn into an exception.",
        };
        return new AnalyzerResult(new Finding(
            "crash.native", Name, Severity.Critical, Confidence.High,
            $"The process crashed with {what} ({hex})" + (where is null ? "" : $" in {Code(where)}"),
            t is null ? "On a thread that runs no .NET code" : $"Thread #{t.Id}" + (t.WhereIsApp ? "" : " · no application frame on its stack"),
            meaning,
            [
                t is null
                    ? "The crashing thread has no managed frames, so its stack needs a native debugger: open the dump in WinDbg and run `!analyze -v`."
                    : $"Open {Chip(EntityUri.Thread(t.Id))}: the managed frames show which P/Invoke or unsafe call was running. The native frames above them need WinDbg (`!analyze -v`).",
                "Review recent changes to P/Invoke signatures, `unsafe` blocks, `Marshal` calls and native dependencies.",
                code == 0xC0000374
                    ? "To catch the write that does the damage, run the app with page heap enabled (`gflags /p /enable app.exe /full`) and take the dump it produces."
                    : "If it can't be reproduced, keep this dump and compare the crashing stack with the next one: the same native module in both is the lead.",
            ],
            [Symptom.Crash],
            t is null ? [] : [context.ThreadRef(t)],
            Scores.For(Severity.Critical, Confidence.High, 1)),
            VerdictHeadline: $"The process crashed with {what}" + (where is null ? "." : $" in {where}."),
            VerdictBody: (t is null ? "It happened on a thread that runs no .NET code. " : $"It happened on {Chip(EntityUri.Thread(t.Id))}. ") + "This is a native exception, not a .NET one, so no `catch` block could have handled it.");
    }

    /// <summary>"`A` calls itself without end", or the cycle in call order for mutual recursion.</summary>
    private static string DescribeLoop(IReadOnlyList<string> loop) => loop.Count switch
    {
        1 => $"{Code(loop[0])} calls itself without end",
        <= 4 => $"{string.Join(" → ", loop.Reverse().Append(loop[^1]).Select(Code))} recurse without end",
        _ => $"{loop.Count} methods call each other in a loop through {Code(loop[0])}",
    };

    /// <summary>The methods of one turn of the recursion, innermost first, or null when the frames don't repeat.</summary>
    private static IReadOnlyList<string>? LoopOf(IReadOnlyList<string> frames) =>
        FindRecursion(frames) is { } r ? frames.Skip(r.Start).Take(r.Period).Where(n => !n.StartsWith('[')).DefaultIfEmpty(frames[r.Start]).ToList() : null;

    /// <summary>
    /// Finds a repeating run of frames: the shortest period (1 for a method calling itself, 2
    /// for A ↔ B, …) that repeats for at least <see cref="MinRepeatedFrames"/> frames, allowing
    /// a few unrelated frames on top.
    /// </summary>
    internal static (int Start, int Period)? FindRecursion(IReadOnlyList<string> frames)
    {
        for (var start = 0; start < Math.Min(24, frames.Count); start++)
        {
            for (var period = 1; period <= MaxPeriod; period++)
            {
                var run = 0;
                while (start + run + period < frames.Count && frames[start + run] == frames[start + run + period])
                {
                    run++;
                }

                if (run + period >= Math.Max(MinRepeatedFrames, period * 8))
                {
                    return (start, period);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The managed methods whose return addresses lie in the deepest part of a thread's stack,
    /// deepest first. Recursion puts the same few return addresses there over and over, so each
    /// distinct value is resolved once.
    /// </summary>
    private static List<string> ScanStack(ClrRuntime runtime, ThreadRecord t, DumpArchitecture architecture)
    {
        var names = new List<string>();
        try
        {
            var thread = runtime.Threads.FirstOrDefault(x => x.OSThreadId == (uint)t.OsId);
            if (thread is null || thread.StackBase <= thread.StackLimit || thread.StackLimit == 0)
            {
                return names;
            }

            var reader = runtime.DataTarget.DataReader;
            var pointerSize = reader.PointerSize;
            var (low, high) = (thread.StackLimit, thread.StackBase);
            var end = Math.Min(high, low + ScanBytes);
            var resolved = new Dictionary<ulong, string?>();
            var page = new byte[4096];
            var lookups = 0;
            for (var address = low; address < end; address += (ulong)page.Length)
            {
                // Read a page at a time: the guard pages at the very bottom may not be in the dump.
                var read = reader.Read(address, page);
                for (var offset = 0; offset + pointerSize <= read; offset += pointerSize)
                {
                    var value = pointerSize == 8 ? BitConverter.ToUInt64(page, offset) : BitConverter.ToUInt32(page, offset);
                    if (value < 0x10000 || (value >= low && value < high))
                    {
                        continue;
                    }

                    if (!resolved.TryGetValue(value, out var name))
                    {
                        if (++lookups > MaxLookups)
                        {
                            return names;
                        }

                        // Stacks also hold pointers to stubs and method entry points; only a value that
                        // follows a call instruction is a frame.
                        name = IsReturnAddress(reader, value, architecture) && runtime.GetMethodByInstructionPointer(value) is { } method
                            ? FrameClassifier.DisplayName(method)
                            : null;
                        resolved[value] = name;
                    }

                    if (name is not null)
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch
        {
            // Raw stack memory is best-effort; the finding is reported without the loop.
        }

        return names;
    }

    private static bool IsReturnAddress(IDataReader reader, ulong address, DumpArchitecture architecture)
    {
        Span<byte> before = stackalloc byte[8];
        return address > 8 && reader.Read(address - 8, before) == before.Length && FollowsCall(before, architecture);
    }

    /// <summary>Whether the bytes just before an address are a call instruction, which is what makes it a return address.</summary>
    internal static bool FollowsCall(ReadOnlySpan<byte> before, DumpArchitecture architecture)
    {
        if (before.Length < 8)
        {
            return false;
        }

        if (architecture == DumpArchitecture.Arm64)
        {
            var instruction = BitConverter.ToUInt32(before[^4..]);
            return (instruction & 0xFC000000) == 0x94000000      // BL
                   || (instruction & 0xFFFFFC1F) == 0xD63F0000;  // BLR
        }

        // x86/x64: CALL rel32 (E8), or CALL r/m (FF /2) in its 2 to 7 byte encodings.
        static bool Indirect(byte opcode, byte modrm) => opcode == 0xFF && (modrm & 0x38) == 0x10;
        return before[^5] == 0xE8
               || Indirect(before[^2], before[^1])
               || Indirect(before[^3], before[^2])
               || Indirect(before[^4], before[^3])
               || Indirect(before[^6], before[^5])
               || Indirect(before[^7], before[^6]);
    }

    internal static string? NativeExceptionName(uint code) => code switch
    {
        0xC0000005 => "an access violation",
        StackOverflowCode => "a stack overflow",
        0xC0000374 => "heap corruption",
        0xC0000409 => "a fail-fast (stack buffer overrun or `__fastfail`)",
        0xC000001D => "an illegal instruction",
        0xC0000094 => "an integer divide by zero",
        0xC0000096 => "a privileged instruction",
        0xC0000006 => "an in-page I/O error",
        0xC0000017 or 0xC000012D => "an out-of-memory condition in native code",
        0xE06D7363 => "an unhandled C++ exception",
        0xC0000420 => "a failed assertion",
        // Breakpoints and debugger events aren't crashes: a dump taken at one is just a snapshot.
        0x80000003 or 0x80000004 or 0x4000001F or 0x40010006 or 0x406D1388 => null,
        _ when (code & 0xC0000000) == 0xC0000000 => "a native exception",
        _ => null,
    };
}
