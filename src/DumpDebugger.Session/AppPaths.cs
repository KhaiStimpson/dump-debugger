namespace DumpDebugger.Session;

/// <summary>Per-user locations for state that isn't tied to one dump (recents, WebView2 profile).</summary>
public static class AppPaths
{
    public static string DataRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DumpDebugger");

    public static string Ensure(string relative)
    {
        var path = Path.Combine(DataRoot, relative);
        Directory.CreateDirectory(path);
        return path;
    }
}
