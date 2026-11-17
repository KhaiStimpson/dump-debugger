using System.Runtime.InteropServices;
using DumpDebugger.Core.Dump;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Dac;

/// <summary>Where DACs may come from for one load: the app cache always; a symbol server only after consent.</summary>
public sealed record DacLookup(string CacheDirectory, DacDownloader? Downloader, Action<string, string>? Progress = null);

/// <summary>
/// Implements the DAC resolution chain from PLAN.md §2.3 over ClrMD's own candidate list
/// (<see cref="ClrInfo.DebuggingLibraries"/>), in ClrMD's order and with its semantics
/// (first loadable candidate wins, the first failure is reported), but resolving each file
/// itself so the result records the DAC actually loaded and where it came from:
/// the dump's runtime folder on this machine (tier 2, a full-path candidate ClrMD only
/// offers when the local runtime binary matches the dump's), the app cache (tier 3), or the
/// symbol server (tier 4, only when <see cref="DacLookup.Downloader"/> is set).
/// </summary>
public static class DacResolver
{
    public const string MicrosoftSymbolServer = "https://msdl.microsoft.com/download/symbols";

    public sealed record ResolutionResult(ClrRuntime? Runtime, DacResolutionTier Tier, string? DacPath, Exception? Error);

    /// <summary>
    /// ClrMD's own file locator: offline and cache-only, so nothing ClrMD does internally reaches
    /// the network. DAC downloads go through <see cref="TryCreateRuntime"/> only.
    /// </summary>
    public static DataTargetOptions BuildOptions(string cacheDirectory) => new()
    {
        SymbolCachePath = cacheDirectory,
        SymbolPaths = [],
        FileLocator = new CachedFileLocator(cacheDirectory),
    };

    public static ResolutionResult TryCreateRuntime(ClrInfo clrInfo, DacLookup lookup, CancellationToken ct = default) =>
        TryCreateRuntime(clrInfo, lookup, _ => true, ct);

    /// <param name="include">Tests narrow ClrMD's candidates (e.g. drop the local-folder one to exercise the cache).</param>
    internal static ResolutionResult TryCreateRuntime(ClrInfo clrInfo, DacLookup lookup, Func<DebugLibraryInfo, bool> include, CancellationToken ct)
    {
        Exception? firstError = null;
        var label = $"{clrInfo.Flavor} {clrInfo.Version}";
        foreach (var candidate in Candidates(clrInfo).Where(include))
        {
            ct.ThrowIfCancellationRequested();
            string? path;
            DacResolutionTier tier;
            try
            {
                (path, tier) = Locate(candidate, lookup, label, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidDataException or IOException)
            {
                firstError ??= ex;
                continue;
            }

            if (path is null)
            {
                continue;
            }

            try
            {
                // ignoreMismatch matches ClrMD's own loop: candidates are already keyed to this runtime build.
                return new ResolutionResult(clrInfo.CreateRuntime(path, ignoreMismatch: true), tier, path, null);
            }
            catch (Exception ex)
            {
                lookup.Progress?.Invoke("DAC candidate failed", $"{label}: {path}: {ex.Message}");
                firstError ??= ex;
            }
        }

        return new ResolutionResult(null, DacResolutionTier.None, null, firstError ?? new FileNotFoundException(lookup.Downloader is null
            ? "Could not find a matching DAC offline (checked this machine's matching runtime folder and the app DAC cache)."
            : $"Could not find a matching DAC on this machine, in the app DAC cache, or on {lookup.Downloader.Server.Host}."));
    }

    /// <summary>ClrMD's filter: a DAC built for this process's platform and architecture.</summary>
    private static IEnumerable<DebugLibraryInfo> Candidates(ClrInfo clrInfo) =>
        clrInfo.DebuggingLibraries.Where(d => d.Kind == DebugLibraryKind.Dac
                                               && d.Platform == OSPlatform.Windows
                                               && d.TargetArchitecture == RuntimeInformation.ProcessArchitecture);

    private static (string? Path, DacResolutionTier Tier) Locate(DebugLibraryInfo candidate, DacLookup lookup, string label, CancellationToken ct)
    {
        if (Path.GetFileName(candidate.FileName) != candidate.FileName)
        {
            return File.Exists(candidate.FileName) ? (candidate.FileName, DacResolutionTier.LocalMachine) : (null, DacResolutionTier.None);
        }

        if (candidate.IndexTimeStamp == 0 || candidate.IndexFileSize == 0)
        {
            return (null, DacResolutionTier.None);
        }

        var ownIdentity = candidate.ArchivedUnder == SymbolProperties.Self;
        if (DacCache.TryGet(lookup.CacheDirectory, candidate.FileName, candidate.IndexTimeStamp, candidate.IndexFileSize, ownIdentity, candidate.TargetArchitecture) is { } cached)
        {
            return (cached, DacResolutionTier.AppCache);
        }

        if (File.Exists(DacCache.KeyPath(lookup.CacheDirectory, candidate.FileName, candidate.IndexTimeStamp, candidate.IndexFileSize)))
        {
            lookup.Progress?.Invoke("DAC cache entry rejected", $"{label}: cached {candidate.FileName} is damaged or doesn't match; it will be replaced if downloaded");
        }

        if (lookup.Downloader is not { } downloader)
        {
            return (null, DacResolutionTier.None);
        }

        lookup.Progress?.Invoke("Downloading DAC", $"{candidate.FileName} for {label} from {downloader.Server.Host}");
        var downloaded = downloader.DownloadAsync(lookup.CacheDirectory, candidate.FileName, candidate.IndexTimeStamp, candidate.IndexFileSize,
                ownIdentity, candidate.TargetArchitecture,
                (done, total) => lookup.Progress?.Invoke("Downloading DAC",
                    $"{candidate.FileName} for {label}: {Megabytes(done)}{(total is { } t ? $" of {Megabytes(t)}" : "")}"), ct)
            .GetAwaiter().GetResult();
        if (downloaded is null)
        {
            lookup.Progress?.Invoke("DAC not on server", $"{candidate.FileName} for {label}");
        }

        return (downloaded, DacResolutionTier.SymbolServer);
    }

    private static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";
}

/// <summary>
/// Picks the runtime analysis runs against when a process hosts several (w3wp can load both
/// .NET Framework and CoreCLR, one of them idle): the loaded runtime with the most managed
/// threads; on a tie CoreCLR, then the earlier one in the dump.
/// </summary>
public static class RuntimeSelection
{
    /// <returns>The index of the primary runtime, or -1 when none loaded (thread count null).</returns>
    public static int ChoosePrimary(IReadOnlyList<(RuntimeFamily Family, int? ManagedThreads)> runtimes)
    {
        var best = -1;
        for (var i = 0; i < runtimes.Count; i++)
        {
            if (runtimes[i].ManagedThreads is not { } threads)
            {
                continue;
            }

            if (best < 0 || threads > runtimes[best].ManagedThreads
                || (threads == runtimes[best].ManagedThreads && runtimes[i].Family == RuntimeFamily.NetCore && runtimes[best].Family != RuntimeFamily.NetCore))
            {
                best = i;
            }
        }

        return best;
    }
}
