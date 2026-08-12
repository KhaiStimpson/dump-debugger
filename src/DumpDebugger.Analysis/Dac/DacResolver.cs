using DumpDebugger.Core.Dump;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Dac;

/// <summary>
/// Implements the four-tier DAC resolution chain from PLAN.md §2.3: embedded-in-dump and
/// local-machine matches are tried offline first (tiers 1-2, handled internally by ClrMD's
/// default file locator against the dump image and local framework directories); the app
/// cache (tier 3) is where resolved DACs are persisted for reuse; the Microsoft symbol
/// server (tier 4) is only consulted when the caller opts in, since it means a network call.
/// </summary>
public static class DacResolver
{
    public const string MicrosoftSymbolServer = "https://msdl.microsoft.com/download/symbols";

    public sealed record ResolutionResult(ClrRuntime? Runtime, DacResolutionTier Tier, Exception? Error);

    /// <summary>
    /// Builds the DataTargetOptions to load the dump with. The app cache directory is always
    /// wired in as the symbol cache (tier 3); the Microsoft symbol server (tier 4) is added
    /// to the search path only when the caller has opted in.
    /// </summary>
    public static DataTargetOptions BuildOptions(bool allowSymbolServer)
    {
        return new DataTargetOptions
        {
            SymbolCachePath = DacCache.GetCacheDirectory(),
            SymbolPaths = allowSymbolServer ? [MicrosoftSymbolServer] : [],
        };
    }

    public static ResolutionResult TryCreateRuntime(ClrInfo clrInfo, bool symbolServerWasAllowed)
    {
        try
        {
            var runtime = clrInfo.CreateRuntime();
            var dacPath = clrInfo.DebuggingLibraries.FirstOrDefault(d => d.Kind == DebugLibraryKind.Dac)?.FileName;
            var tier = InferTier(dacPath, symbolServerWasAllowed);
            return new ResolutionResult(runtime, tier, null);
        }
        catch (Exception ex)
        {
            return new ResolutionResult(null, DacResolutionTier.None, ex);
        }
    }

    private static DacResolutionTier InferTier(string? dacPath, bool symbolServerWasAllowed)
    {
        if (dacPath is null)
        {
            return DacResolutionTier.None;
        }

        if (dacPath.StartsWith(DacCache.GetCacheDirectory(), StringComparison.OrdinalIgnoreCase))
        {
            return symbolServerWasAllowed ? DacResolutionTier.SymbolServer : DacResolutionTier.AppCache;
        }

        return dacPath.Contains("Microsoft.NET", StringComparison.OrdinalIgnoreCase)
               || dacPath.Contains(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
            ? DacResolutionTier.LocalMachine
            : DacResolutionTier.EmbeddedInDump;
    }
}
