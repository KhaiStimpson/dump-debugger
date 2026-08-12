using DumpDebugger.Core.Dump;

namespace DumpDebugger_App.ViewModels;

/// <summary>
/// One row in the Threads grid: N threads sharing an identical stack collapse into one row
/// (PLAN.md §3.2 "stack group" column — "200 threads with identical stacks collapse to one
/// row with a count of 200").
/// </summary>
public sealed record ThreadGroupRow(
    string StackGroupHash,
    int Count,
    string TopFrame,
    string FullStack,
    IReadOnlyList<int> OSThreadIds,
    bool IsGc,
    bool IsFinalizer,
    int MaxLockCount,
    string? ExceptionType,
    string? OperationContext)
{
    public bool IsBlocked => MaxLockCount > 0;


    public static IReadOnlyList<ThreadGroupRow> FromThreads(IReadOnlyList<ThreadInfo> threads) =>
        threads
            .GroupBy(t => t.StackGroupHash)
            .Select(g =>
            {
                var first = g.First();
                var topFrame = first.Frames.Count > 0
                    ? $"{first.Frames[0].TypeName}.{first.Frames[0].MethodName}"
                    : "<no managed frames>";
                var fullStack = string.Join(
                    Environment.NewLine,
                    first.Frames.Select(f => $"{f.TypeName}.{f.MethodName}"));

                return new ThreadGroupRow(
                    g.Key,
                    g.Count(),
                    topFrame,
                    fullStack,
                    g.Select(t => t.OSThreadId).ToList(),
                    g.Any(t => t.IsGc),
                    g.Any(t => t.IsFinalizer),
                    g.Max(t => t.LockCount),
                    g.Select(t => t.CurrentExceptionType).FirstOrDefault(e => e is not null),
                    first.OperationContext is null
                        ? null
                        : $"[{first.OperationContext.Source}] {first.OperationContext.Description}");
            })
            .OrderByDescending(r => r.Count)
            .ToList();
}
