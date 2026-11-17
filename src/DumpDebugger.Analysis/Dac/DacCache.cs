namespace DumpDebugger.Analysis.Dac;

/// <summary>
/// App-managed DAC cache directory (§2.3 tier 3). ClrMD populates it on download and
/// CachedFileLocator reads it offline; this centralizes where it lives so the
/// cache survives across workspaces and app versions.
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
}
