namespace DumpDebugger.Core.Contracts;

public static class Engine
{
    /// <summary>
    /// Bump whenever analyzers or the diagnosis shape change: workspace caches written by a
    /// different engine version are ignored and the dump is re-analyzed.
    /// </summary>
    public const string Version = "2.1";
}
