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
    /// <summary>Opens with the app DAC cache, and the Microsoft symbol server only when <paramref name="allowSymbolServer"/> (user consent).</summary>
    public static LoadedDump Load(string dumpPath, bool allowSymbolServer = false, Action<string, string>? progress = null, CancellationToken ct = default)
    {
        using var downloader = allowSymbolServer ? new DacDownloader(DacDownloader.MicrosoftSymbolServer, DacDownloader.DefaultTimeout) : null;
        return Load(dumpPath, new DacLookup(DacCache.GetCacheDirectory(), downloader, progress), ct);
    }

    public static LoadedDump Load(string dumpPath, DacLookup lookup, CancellationToken ct = default)
    {
        var progress = lookup.Progress;
        var options = DacResolver.BuildOptions(lookup.CacheDirectory);
        progress?.Invoke("Opening dump", $"Symbols: {(lookup.Downloader is { } d ? $"{d.Server.Host} allowed" : "offline only")}; cache: {lookup.CacheDirectory}");
        var dataTarget = DataTarget.LoadDump(dumpPath, options);
        try
        {
            return Describe(dumpPath, dataTarget, lookup, ct);
        }
        catch
        {
            dataTarget.Dispose();
            throw;
        }
    }

    private static LoadedDump Describe(string dumpPath, DataTarget dataTarget, DacLookup lookup, CancellationToken ct)
    {
        var progress = lookup.Progress;
        var architecture = MapArchitecture(dataTarget.DataReader.Architecture);
        var found = new List<(RuntimeInfo Info, ClrRuntime? Runtime)>();

        foreach (var clrInfo in dataTarget.ClrVersions)
        {
            var label = $"{clrInfo.Flavor} {ProductVersion(clrInfo)} (build {clrInfo.Version})";
            progress?.Invoke("Resolving DAC", label);
            var resolution = DacResolver.TryCreateRuntime(clrInfo, lookup, ct);
            var threads = resolution.Runtime is { } runtime ? CountThreads(runtime) : (int?)null;
            progress?.Invoke(resolution.Runtime is not null ? "Runtime loaded" : "DAC resolution failed",
                resolution.Runtime is not null
                    ? $"{label}: {resolution.Tier} DAC {resolution.DacPath}; {threads} managed threads"
                    : $"{label}: {resolution.Error?.Message}");
            var family = clrInfo.Flavor == ClrFlavor.Desktop ? RuntimeFamily.NetFramework : RuntimeFamily.NetCore;

            found.Add((new RuntimeInfo(
                family,
                clrInfo.Version.ToString(),
                ProductVersion(clrInfo),
                resolution.DacPath,
                resolution.Tier,
                resolution.Runtime is not null,
                resolution.Error?.Message,
                threads), resolution.Runtime));
        }

        // Primary first: everything that reads "the" runtime (analysis, labels, the DAC tier) uses it.
        var primary = RuntimeSelection.ChoosePrimary(found.Select(f => (f.Info.Family, f.Info.ManagedThreadCount)).ToList());
        var ordered = primary < 0 ? found : found.Skip(primary).Take(1).Concat(found.Where((_, i) => i != primary)).ToList();
        var runtimeInfos = ordered.Select((f, i) => primary >= 0 && i == 0 ? f.Info with { IsPrimary = true } : f.Info).ToList();
        var runtimes = ordered.Where(f => f.Runtime is not null).Select(f => f.Runtime!).ToList();
        if (primary >= 0)
        {
            progress?.Invoke("Primary runtime", $"{runtimeInfos[0].Family} {runtimeInfos[0].ProductVersion} ({runtimeInfos[0].ManagedThreadCount} managed threads)");
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
            ThreadCount: primary >= 0 ? runtimeInfos[0].ManagedThreadCount ?? 0 : 0,
            ModuleCount: modules.Count,
            CrashThreadOsId: header.CrashThreadOsId,
            CrashExceptionCode: header.ExceptionCode);

        return new LoadedDump
        {
            DataTarget = dataTarget,
            Metadata = metadata,
            Runtimes = runtimes,
        };
    }

    private static int CountThreads(ClrRuntime runtime)
    {
        try
        {
            return runtime.Threads.Length;
        }
        catch (Exception)
        {
            return 0; // a runtime whose thread list can't be read is never the better choice
        }
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
