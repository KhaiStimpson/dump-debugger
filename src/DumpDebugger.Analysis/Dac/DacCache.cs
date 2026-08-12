namespace DumpDebugger.Analysis.Dac;

/// <summary>
/// App-managed DAC cache directory (§2.3 tier 3). ClrMD's own SymbolCachePath does the
/// actual population/lookup; this just centralizes where that directory lives so the
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
