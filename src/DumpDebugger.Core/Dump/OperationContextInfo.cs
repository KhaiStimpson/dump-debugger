namespace DumpDebugger.Core.Dump;

/// <summary>
/// PLAN.md §4.1 "Operation context" — turns "thread 47 is blocked in Monitor.Enter" into
/// "thread 47 is serving POST /api/orders/checkout and is blocked in Monitor.Enter".
/// </summary>
public sealed record OperationContextInfo(
    int OSThreadId,
    string Source,
    string Description);
