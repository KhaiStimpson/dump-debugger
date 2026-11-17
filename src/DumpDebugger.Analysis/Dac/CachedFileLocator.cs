using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Dac;

/// <summary>ClrMD's default locator only checks its cache through a symbol server. Check it offline too.</summary>
internal sealed class CachedFileLocator(string cacheDirectory, IFileLocator? online) : IFileLocator
{
    public string? FindPEImage(string fileName, int buildTimeStamp, int imageSize, bool checkProperties)
    {
        var name = Path.GetFileName(fileName).ToLowerInvariant();
        var key = $"{unchecked((uint)buildTimeStamp):x8}{unchecked((uint)imageSize):x}";
        var path = Path.Combine(cacheDirectory, name, key, name);
        if (File.Exists(path) && (!checkProperties || MatchesImage(path, buildTimeStamp, imageSize)))
        {
            return path;
        }

        return online?.FindPEImage(fileName, buildTimeStamp, imageSize, checkProperties);
    }

    public string? FindPEImage(string fileName, SymbolProperties archivedUnder, ImmutableArray<byte> buildIdOrUUID,
        OSPlatform originalPlatform, bool checkProperties)
    {
        if (!buildIdOrUUID.IsDefaultOrEmpty && (originalPlatform == OSPlatform.Linux || originalPlatform == OSPlatform.OSX))
        {
            var name = Path.GetFileName(fileName).ToLowerInvariant();
            var platform = originalPlatform == OSPlatform.Linux ? "elf-buildid" : "mach-uuid";
            var prefix = archivedUnder == SymbolProperties.Coreclr ? "coreclr-" : "";
            var path = Path.Combine(cacheDirectory, name, $"{platform}-{prefix}{Convert.ToHexStringLower(buildIdOrUUID.AsSpan())}", name);
            if (File.Exists(path)) return path;
        }

        return online?.FindPEImage(fileName, archivedUnder, buildIdOrUUID, originalPlatform, checkProperties);
    }

    private static bool MatchesImage(string path, int timestamp, int size)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.CoffHeader.TimeDateStamp == timestamp && pe.PEHeaders.PEHeader?.SizeOfImage == size;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException)
        {
            return false;
        }
    }
}
