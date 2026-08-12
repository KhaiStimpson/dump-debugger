using System.Security.Cryptography;
using System.Text;
using DumpDebugger.Core.Dump;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis;

/// <summary>
/// Phase 1 (PLAN.md §3.2/§4.1): enumerates threads and walks managed stacks, computing a
/// stable hash per thread so the UI can collapse threads with identical stacks into one row
/// with a count ("stack grouping" — the highest-value thread analyzer).
/// </summary>
public static class ThreadEnumerator
{
    private const int MaxFrames = 200;

    public static IReadOnlyList<ThreadInfo> Enumerate(ClrRuntime runtime)
    {
        var threads = new List<ThreadInfo>();

        foreach (var clrThread in runtime.Threads)
        {
            var frames = new List<StackFrameInfo>();
            foreach (var frame in clrThread.EnumerateStackTrace(false, MaxFrames))
            {
                if (frame.Kind == ClrStackFrameKind.ManagedMethod && frame.Method is not null)
                {
                    frames.Add(new StackFrameInfo(frame.Method.Name ?? "<unknown>", frame.Method.Type?.Name));
                }
                else
                {
                    frames.Add(new StackFrameInfo(frame.FrameName ?? "<runtime frame>", null));
                }
            }

            threads.Add(new ThreadInfo(
                OSThreadId: (int)clrThread.OSThreadId,
                ManagedThreadId: clrThread.ManagedThreadId,
                IsAlive: clrThread.IsAlive,
                IsGc: clrThread.IsGc,
                IsFinalizer: clrThread.IsFinalizer,
                LockCount: (int)clrThread.LockCount,
                CurrentExceptionType: clrThread.CurrentException?.Type?.Name,
                Frames: frames,
                StackGroupHash: HashFrames(frames)));
        }

        return threads;
    }

    private static string HashFrames(IReadOnlyList<StackFrameInfo> frames)
    {
        if (frames.Count == 0)
        {
            return "empty";
        }

        var sb = new StringBuilder();
        foreach (var frame in frames)
        {
            sb.Append(frame.TypeName).Append('.').Append(frame.MethodName).Append('|');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes)[..16];
    }
}
