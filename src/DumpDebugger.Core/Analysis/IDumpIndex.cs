using DumpDebugger.Core.Dump;

namespace DumpDebugger.Core.Analysis;

/// <summary>
/// Normalized view over an indexed dump that analyzers query. Implemented by
/// DumpDebugger.Analysis against ClrMD; kept ClrMD-free here so analyzer
/// contracts can be referenced without pulling in the native DAC dependency.
/// </summary>
public interface IDumpIndex
{
    DumpMetadata Metadata { get; }
}

public interface IAnalyzer
{
    string Name { get; }

    IReadOnlyList<Findings.Finding> Analyze(IDumpIndex index);
}
