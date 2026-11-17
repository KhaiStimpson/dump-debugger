using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Dac;

/// <summary>
/// The only file locator ClrMD gets: the app cache, offline, validated. ClrMD never reaches
/// the network itself; DAC downloads happen only in DacResolver, only after consent, so other
/// lookups ClrMD makes (module images, for example) can't send identifiers anywhere.
/// </summary>
internal sealed class CachedFileLocator(string cacheDirectory) : IFileLocator
{
    public string? FindPEImage(string fileName, int buildTimeStamp, int imageSize, bool checkProperties)
    {
        var path = DacCache.KeyPath(cacheDirectory, fileName, buildTimeStamp, imageSize);
        return File.Exists(path) && (!checkProperties || DacImage.IsUsable(path, RuntimeInformation.ProcessArchitecture, (buildTimeStamp, imageSize)))
            ? path
            : null;
    }

    public string? FindPEImage(string fileName, SymbolProperties archivedUnder, ImmutableArray<byte> buildIdOrUUID,
        OSPlatform originalPlatform, bool checkProperties) => null; // Windows dumps only.
}
