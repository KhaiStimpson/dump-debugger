using System.Runtime.InteropServices;
using DumpDebugger.Analysis.Dac;
using DumpDebugger.Core.Dump;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis;

public sealed class LoadedDump : IDisposable
{
    public required DataTarget DataTarget { get; init; }
    public required DumpMetadata Metadata { get; init; }
    public required IReadOnlyList<ClrRuntime> Runtimes { get; init; }

    public void Dispose() => DataTarget.Dispose();
}

/// <summary>
/// Opens a dump via ClrMD and produces DumpMetadata (Phase 0 deliverable: "the app opens a
/// dump of any supported runtime and tells you what it is"). Architecture matching between
/// this process and the dump is a hard ClrMD requirement (PLAN.md §2.2) — the caller (the
/// Worker) must already be running as the matching x86/x64 process before calling this.
/// </summary>
public static class DumpLoader
{
    public static LoadedDump Load(string dumpPath, bool allowSymbolServer = false)
    {
        var options = DacResolver.BuildOptions(allowSymbolServer);
        var dataTarget = DataTarget.LoadDump(dumpPath, options);

        var architecture = MapArchitecture(dataTarget.DataReader.Architecture);
        var runtimeInfos = new List<RuntimeInfo>();
        var runtimes = new List<ClrRuntime>();

        foreach (var clrInfo in dataTarget.ClrVersions)
        {
            var resolution = DacResolver.TryCreateRuntime(clrInfo, allowSymbolServer);
            var family = clrInfo.Flavor == ClrFlavor.Desktop ? RuntimeFamily.NetFramework : RuntimeFamily.NetCore;
            var dacPath = clrInfo.DebuggingLibraries.FirstOrDefault(d => d.Kind == DebugLibraryKind.Dac)?.FileName;

            runtimeInfos.Add(new RuntimeInfo(
                family,
                clrInfo.Version.ToString(),
                ProductVersion(clrInfo),
                dacPath,
                resolution.Tier,
                resolution.Runtime is not null));

            if (resolution.Runtime is not null)
            {
                runtimes.Add(resolution.Runtime);
            }
        }

        var header = MinidumpHeader.Read(dumpPath);
        var modules = dataTarget.EnumerateModules().ToList();
        var exe = modules.FirstOrDefault(m => m.FileName?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
        var metadata = new DumpMetadata(
            DumpPath: dumpPath,
            FileSizeBytes: new FileInfo(dumpPath).Length,
            Architecture: architecture,
            IsFullMemoryDump: header.IsFullMemory,
            IsCrashDump: header.HasException,
            CapturedAt: header.CapturedAt,
            ProcessName: exe?.FileName is { } exePath ? Path.GetFileName(exePath) : null,
            ProcessId: dataTarget.DataReader.ProcessId,
            Runtimes: runtimeInfos,
            ThreadCount: runtimes.Count > 0 ? runtimes[0].Threads.Count() : 0,
            ModuleCount: modules.Count);

        return new LoadedDump
        {
            DataTarget = dataTarget,
            Metadata = metadata,
            Runtimes = runtimes,
        };
    }

    /// <summary>
    /// The version people recognise ("10.0.15", "4.8"). The CLR file version (10.0.1526.53008) is
    /// not it; on .NET Core the shared-framework folder the runtime was loaded from is.
    /// </summary>
    private static string ProductVersion(ClrInfo clr)
    {
        var path = clr.ModuleInfo.FileName ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(path, @"Microsoft\.NETCore\.App[\\/](\d+\.\d+\.\d+[^\\/]*)");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        return $"{clr.Version.Major}.{clr.Version.Minor}";
    }

    private static DumpArchitecture MapArchitecture(Architecture arch) => arch switch
    {
        Architecture.X86 => DumpArchitecture.X86,
        Architecture.X64 => DumpArchitecture.X64,
        Architecture.Arm64 => DumpArchitecture.Arm64,
        _ => DumpArchitecture.Unknown,
    };
}
