using DumpDebugger.Core.Source;

namespace DumpDebugger.Core.Dump;

/// <summary>
/// PLAN.md §4.1 "Operation context" — turns "thread 47 is blocked in Monitor.Enter" into
/// "thread 47 is serving POST /api/orders/checkout and is blocked in Monitor.Enter".
/// </summary>
/// <param name="Source">Where this context came from: "HttpContext" or "GenericFrame" — not to
/// be confused with <paramref name="Location"/>, which is the resolved source-code location.</param>
/// <param name="Location">The application frame's source location, when Source Link resolved
/// one (see DumpDebugger.Analysis.SourceLink) — null if the module has no PDB reachable locally.</param>
public sealed record OperationContextInfo(
    int OSThreadId,
    string Source,
    string Description,
    SourceLocation? Location = null);
