using System.Security.Cryptography;
using System.Text;
using DumpDebugger.Analysis.Frames;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;
using StackFrame = DumpDebugger.Core.Contracts.StackFrame;

namespace DumpDebugger.Analysis.Threads;

public enum WaitKind
{
    None,
    Lock,
    Task,
    Event,
    Sleep,
    Join,
    IdlePool,
    Io,
    Timer,
    NoManagedCode,
}

/// <summary>An object referenced from a thread's stack, and the frame that references it.</summary>
public readonly record struct StackRootObject(ulong Address, string? TypeName, int FrameIndex, bool IsAppType);

/// <summary>Everything the analyzers know about one thread. Built once per dump, then read-only except for lock annotations.</summary>
public sealed class ThreadRecord
{
    public required int Id { get; init; }
    public required int OsId { get; init; }
    public required ulong ClrAddress { get; init; }
    public required ThreadRole Role { get; init; }
    public required WaitKind Wait { get; init; }
    public required IReadOnlyList<StackFrame> Frames { get; init; }
    public required IReadOnlyList<ClrMethod?> Methods { get; init; }
    public required IReadOnlyList<StackRootObject> Roots { get; init; }
    public required string? Where { get; init; }
    public required string? WhereName { get; init; }
    public required bool WhereIsApp { get; init; }
    public required string? Request { get; init; }
    public required string? RequestPath { get; init; }
    public required string? Exception { get; init; }
    public required string StackSignature { get; init; }

    public ThreadState State { get; set; }
    public string StateLabel { get; set; } = "";

    /// <summary>Set by the lock model.</summary>
    public ulong? WaitingOnLock { get; set; }
    public int? WaitingOnThread { get; set; }
    public bool InDeadlock { get; set; }
    public List<ulong> Holds { get; } = [];

    public int FirstAppFrameIndex => Frames.ToList().FindIndex(f => f.Kind == FrameKind.App);

    public string GroupKey => WhereIsApp
        ? $"stack:{StackSignature}:{StateLabel}"
        : $"noise:{Where}:{State}:{StateLabel}";

    public ThreadSummary ToSummary() => new(
        Id, OsId, State, StateLabel, Role, Where, Request, RequestPath, GroupKey,
        WaitingOnLock is { } l ? EntityUri.Lock(l) : null, WaitingOnThread, InDeadlock, Exception);
}

/// <summary>
/// Builds <see cref="ThreadRecord"/>s: classified frames, what the thread is waiting on (from
/// its top frames), its role, the request it serves (from Kestrel/ASP.NET objects among its
/// stack roots), and a signature over its application frames for grouping.
/// </summary>
public static class ThreadModelBuilder
{
    private const int MaxFrames = 200;

    public static IReadOnlyList<ThreadRecord> Build(ClrRuntime runtime, SourceLocator? sources, CancellationToken ct)
    {
        var result = new List<ThreadRecord>();
        foreach (var thread in runtime.Threads)
        {
            ct.ThrowIfCancellationRequested();
            if (!thread.IsAlive)
            {
                continue;
            }

            result.Add(BuildOne(thread, sources));
        }

        return result;
    }

    private static ThreadRecord BuildOne(ClrThread thread, SourceLocator? sources)
    {
        var frames = new List<StackFrame>();
        var methods = new List<ClrMethod?>();
        var stackPointers = new List<ulong>();
        try
        {
            foreach (var f in thread.EnumerateStackTrace(false, MaxFrames))
            {
                frames.Add(FrameClassifier.Classify(f, frames.Count == 0, sources));
                methods.Add(f.Kind == ClrStackFrameKind.ManagedMethod ? f.Method : null);
                stackPointers.Add(f.StackPointer);
            }
        }
        catch
        {
            // A torn stack still yields whatever frames were read.
        }

        var roots = ReadRoots(thread, stackPointers);
        var managed = methods.Select((m, i) => (m, i)).Where(x => x.m is not null).Select(x => (Method: x.m!, Index: x.i)).ToList();
        var (wait, waitLabel) = ClassifyWait(managed.Select(x => x.Method).ToList(), frames);
        var request = RequestContextReader.Read(thread, roots);
        var role = ClassifyRole(thread, managed.Select(x => x.Method).ToList(), request is not null);

        var appIndex = frames.FindIndex(f => f.Kind == FrameKind.App);
        string? where;
        string? whereName = null;
        var whereIsApp = appIndex >= 0;
        if (whereIsApp)
        {
            where = frames[appIndex].Display;
            whereName = methods[appIndex] is { } m ? FrameClassifier.DisplayName(m) : where;
        }
        else
        {
            where = NoiseLabel(thread, role, wait, frames);
        }

        var (state, stateLabel) = StateFor(wait, waitLabel, role, whereIsApp);
        var signature = Hash(frames.Where(f => f.Kind == FrameKind.App).Select(f => f.Display).Append(wait.ToString()));

        string? exception = null;
        try
        {
            exception = thread.CurrentException?.Type?.Name;
        }
        catch
        {
        }

        return new ThreadRecord
        {
            Id = thread.ManagedThreadId,
            OsId = (int)thread.OSThreadId,
            ClrAddress = thread.Address,
            Role = role,
            Wait = wait,
            Frames = frames,
            Methods = methods,
            Roots = roots,
            Where = where,
            WhereName = whereName,
            WhereIsApp = whereIsApp,
            Request = request?.Route,
            RequestPath = request?.Path,
            Exception = exception,
            StackSignature = signature,
            State = state,
            StateLabel = stateLabel,
        };
    }

    private static List<StackRootObject> ReadRoots(ClrThread thread, List<ulong> stackPointers)
    {
        var roots = new List<StackRootObject>();
        var seen = new HashSet<ulong>();
        try
        {
            foreach (var root in thread.EnumerateStackRoots())
            {
                var obj = root.Object;
                if (!obj.IsValid || obj.Type is null || !seen.Add(obj.Address))
                {
                    continue;
                }

                var frameIndex = root.StackFrame is { } sf ? stackPointers.IndexOf(sf.StackPointer) : -1;
                roots.Add(new StackRootObject(obj.Address, obj.Type.Name, frameIndex, FrameClassifier.IsAppType(obj.Type.Name, obj.Type.Module)));
            }
        }
        catch
        {
            // Stack roots are best-effort on damaged stacks.
        }

        return roots;
    }

    private static (WaitKind, string) ClassifyWait(IReadOnlyList<ClrMethod> managed, IReadOnlyList<StackFrame> frames)
    {
        if (managed.Count == 0)
        {
            return (WaitKind.NoManagedCode, "No managed code");
        }

        // Look through the framework frames above the first application frame.
        foreach (var m in managed.Take(14))
        {
            var type = m.Type?.Name ?? "";
            var name = m.Name ?? "";
            if (type == "System.Threading.Monitor" && name is "Enter" or "Enter_Slowpath" or "ReliableEnter" or "TryEnter")
            {
                return (WaitKind.Lock, "Waiting for lock");
            }

            if (type == "System.Threading.Lock" && name.StartsWith("Enter", StringComparison.Ordinal))
            {
                return (WaitKind.Lock, "Waiting for lock");
            }

            if (name is "InternalWaitCore" or "SpinThenBlockingWait" or "InternalWait" || (type.StartsWith("System.Runtime.CompilerServices.TaskAwaiter", StringComparison.Ordinal) && name == "GetResult")
                || (type.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) && name is "Wait" or "get_Result" or "GetResultCore" or "WaitAll" or "WaitAny"))
            {
                var label = managed.Any(x => x.Name is "get_Result" or "GetResultCore") ? "Task.Result" : managed.Any(x => x.Name == "GetResult") ? "GetAwaiter().GetResult()" : "Task.Wait()";
                return (WaitKind.Task, label);
            }

            if (type == "System.Threading.Thread" && name is "Sleep" or "SleepInternal")
            {
                return (WaitKind.Sleep, "Sleeping");
            }

            if (type == "System.Threading.Thread" && name == "Join")
            {
                return (WaitKind.Join, "Thread.Join");
            }

            if (type == "System.Threading.LowLevelLifoSemaphore" || (type.Contains("PortableThreadPool+WorkerThread", StringComparison.Ordinal) && name == "WorkerThreadStart"))
            {
                return (WaitKind.IdlePool, "Idle pool");
            }

            if (type.Contains("IOCompletionPoller", StringComparison.Ordinal) || type.Contains("PortableThreadPool+WaitThread", StringComparison.Ordinal)
                || type.StartsWith("System.Net.Sockets", StringComparison.Ordinal))
            {
                return (WaitKind.Io, "Waiting · I/O");
            }

            if (type.Contains("TimerQueue", StringComparison.Ordinal) || type.Contains("PortableThreadPool+GateThread", StringComparison.Ordinal)
                || type.EndsWith("Heartbeat", StringComparison.Ordinal))
            {
                return (WaitKind.Timer, "Timer");
            }

            if (name is "Wait" or "WaitOne" or "WaitOneNoCheck" or "WaitAny" or "WaitAnyMultiple" or "WaitMultiple" or "WaitCore"
                && (type.StartsWith("System.Threading.", StringComparison.Ordinal)))
            {
                var label = type switch
                {
                    "System.Threading.SemaphoreSlim" => "SemaphoreSlim.Wait",
                    "System.Threading.ManualResetEventSlim" => "Waiting · event",
                    "System.Threading.Monitor" => "Monitor.Wait",
                    _ => "Waiting · handle",
                };

                // Keep scanning: a ManualResetEventSlim.Wait under Task.Wait is a task wait.
                var deeper = managed.Skip(managed.ToList().IndexOf(m) + 1).Take(8).ToList();
                if (deeper.Any(x => x.Name is "InternalWaitCore" or "SpinThenBlockingWait" or "get_Result" or "GetResultCore"
                                     || (x.Type?.Name?.StartsWith("System.Runtime.CompilerServices.TaskAwaiter", StringComparison.Ordinal) == true && x.Name == "GetResult")))
                {
                    continue;
                }

                if (deeper.Any(x => x.Type?.Name == "System.Threading.LowLevelLifoSemaphore" || x.Name == "WorkerThreadStart") && !frames.Any(f => f.Kind == FrameKind.App))
                {
                    return (WaitKind.IdlePool, "Idle pool");
                }

                if (deeper.Any(x => x.Type?.Name?.Contains("PortableThreadPool+WaitThread", StringComparison.Ordinal) ?? false))
                {
                    return (WaitKind.Io, "Waiting · I/O");
                }

                if (deeper.Any(x => (x.Type?.Name?.EndsWith("Heartbeat", StringComparison.Ordinal) ?? false)
                                    || (x.Type?.Name?.Contains("TimerQueue", StringComparison.Ordinal) ?? false)
                                    || (x.Type?.Name?.Contains("PortableThreadPool+GateThread", StringComparison.Ordinal) ?? false)))
                {
                    return (WaitKind.Timer, "Timer");
                }

                return (WaitKind.Event, label);
            }

            if (frames.Any(f => f.Kind == FrameKind.App) && FrameClassifier.IsAppType(m.Type?.Name, m.Type?.Module))
            {
                break; // reached application code without seeing a wait
            }
        }

        return (WaitKind.None, "Running");
    }

    private static ThreadRole ClassifyRole(ClrThread thread, IReadOnlyList<ClrMethod> managed, bool hasRequest)
    {
        if (thread.IsFinalizer)
        {
            return ThreadRole.Finalizer;
        }

        if (thread.IsGc)
        {
            return ThreadRole.Gc;
        }

        if (hasRequest)
        {
            return ThreadRole.Request;
        }

        var types = managed.Select(m => m.Type?.Name ?? "").ToList();
        if (types.Any(t => t.Contains("PortableThreadPool+WorkerThread", StringComparison.Ordinal) || t == "System.Threading.ThreadPoolWorkQueue"))
        {
            return ThreadRole.PoolWorker;
        }

        if (types.Any(t => t.Contains("IOCompletionPoller", StringComparison.Ordinal) || t.Contains("PortableThreadPool+WaitThread", StringComparison.Ordinal)))
        {
            return ThreadRole.Io;
        }

        if (types.Any(t => t.Contains("TimerQueue", StringComparison.Ordinal) || t.Contains("GateThread", StringComparison.Ordinal) || t.EndsWith("Heartbeat", StringComparison.Ordinal)))
        {
            return ThreadRole.Timer;
        }

        if (managed.Count > 0 && managed[^1].Name is "Main" or "<Main>" or "<Main>$")
        {
            return ThreadRole.Main;
        }

        return ThreadRole.Background;
    }

    private static (ThreadState, string) StateFor(WaitKind wait, string label, ThreadRole role, bool hasApp) => wait switch
    {
        WaitKind.Lock => (ThreadState.Blocked, label),
        WaitKind.Task => (ThreadState.Waiting, label),
        WaitKind.Event or WaitKind.Sleep or WaitKind.Join => (hasApp || role == ThreadRole.Main ? ThreadState.Waiting : ThreadState.Idle, label),
        WaitKind.Io => (ThreadState.Waiting, label),
        WaitKind.IdlePool or WaitKind.Timer or WaitKind.NoManagedCode => (ThreadState.Idle, wait == WaitKind.IdlePool ? "Idle" : label),
        _ when role is ThreadRole.Finalizer or ThreadRole.Gc && !hasApp => (ThreadState.Idle, "Idle"),
        _ => (ThreadState.Running, "Running"),
    };

    private static string NoiseLabel(ClrThread thread, ThreadRole role, WaitKind wait, IReadOnlyList<StackFrame> frames)
    {
        if (role == ThreadRole.Finalizer)
        {
            return "Finalizer";
        }

        if (role == ThreadRole.Gc)
        {
            return "GC";
        }

        return wait switch
        {
            WaitKind.IdlePool => "— idle thread-pool workers —",
            WaitKind.Io => "Thread-pool I/O",
            WaitKind.Timer => frames.Any(f => f.FullName?.Contains("Kestrel", StringComparison.Ordinal) == true) ? "Kestrel heartbeat" : "Timer / thread-pool gate",
            WaitKind.NoManagedCode => "Native threads (no managed frames)",
            _ => frames.FirstOrDefault(f => f.Kind == FrameKind.Framework)?.Display ?? "Unknown",
        };
    }

    private static string Hash(IEnumerable<string> parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return Convert.ToHexString(bytes)[..12];
    }
}
