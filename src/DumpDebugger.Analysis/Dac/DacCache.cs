using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace DumpDebugger.Analysis.Dac;

/// <summary>
/// App-managed DAC cache directory (§2.3 tier 3), laid out like a symbol-server cache
/// (<c>name\{timestamp:x8}{size:x}\name</c>, the key ClrMD uses), so it survives across
/// workspaces and app versions. DacDownloader fills it; DacResolver reads it offline.
/// </summary>
public static class DacCache
{
    public static string GetCacheDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = Path.Combine(root, "DumpDebugger", "DacCache");
        Directory.CreateDirectory(path);
        return path;
    }

    public static string KeyPath(string cacheDirectory, string fileName, int timestamp, int imageSize)
    {
        var name = Path.GetFileName(fileName).ToLowerInvariant();
        return Path.Combine(cacheDirectory, name, $"{unchecked((uint)timestamp):x8}{unchecked((uint)imageSize):x}", name);
    }

    /// <summary>
    /// The cached file for this key, or null if it is missing or not a complete image of the
    /// expected architecture. A damaged entry reads as missing (so it is downloaded again with
    /// consent, replacing it); it is never handed to ClrMD.
    /// </summary>
    public static string? TryGet(string cacheDirectory, string fileName, int timestamp, int imageSize, bool keyIsOwnIdentity, Architecture architecture)
    {
        var path = KeyPath(cacheDirectory, fileName, timestamp, imageSize);
        return File.Exists(path) && DacImage.IsUsable(path, architecture, keyIsOwnIdentity ? (timestamp, imageSize) : null) ? path : null;
    }
}

/// <summary>Structural checks that catch truncated or wrong files before ClrMD loads them as a DAC.</summary>
public static class DacImage
{
    /// <param name="identity">
    /// The PE TimeDateStamp/SizeOfImage the file must carry, when the cache key is the file's own
    /// identity. DACs archived under the runtime module's key (ClrMD's SymbolProperties.Coreclr)
    /// legitimately carry different values, so only the structure is checked for them.
    /// </param>
    public static bool IsUsable(string path, Architecture architecture, (int Timestamp, int ImageSize)? identity)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            if (headers.PEHeader is null || !headers.IsDll || headers.CoffHeader.Machine != MachineFor(architecture))
            {
                return false;
            }

            if (identity is { } id && (headers.CoffHeader.TimeDateStamp != id.Timestamp || headers.PEHeader.SizeOfImage != id.ImageSize))
            {
                return false;
            }

            // A truncated download still has intact headers: every section's raw data must be present.
            var end = headers.SectionHeaders.Select(s => (long)s.PointerToRawData + s.SizeOfRawData).DefaultIfEmpty(0).Max();
            return end <= stream.Length;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Machine MachineFor(Architecture architecture) => architecture switch
    {
        Architecture.X86 => Machine.I386,
        Architecture.X64 => Machine.Amd64,
        Architecture.Arm64 => Machine.Arm64,
        _ => Machine.Unknown,
    };
}
