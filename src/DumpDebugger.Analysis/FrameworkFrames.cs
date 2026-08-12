namespace DumpDebugger.Analysis;

/// <summary>
/// Shared "is this a framework frame or application code" heuristic, used by
/// OperationContextAnalyzer (finding the deepest application frame) and HotPathAnalyzer
/// (finding shared application-code choke points — every thread trivially shares runtime
/// frames like ThreadPoolWorkQueue.Dispatch, so those must be filtered for either to be useful).
/// PLAN.md §11 open question 3: ship a default list, make it user-editable later.
/// </summary>
internal static class FrameworkFrames
{
    private static readonly string[] NamespacePrefixes =
        ["System.", "Microsoft.", "netstandard", "Internal.", "<"];

    public static bool IsFrameworkType(string typeName) =>
        NamespacePrefixes.Any(prefix => typeName.StartsWith(prefix, StringComparison.Ordinal));
}
