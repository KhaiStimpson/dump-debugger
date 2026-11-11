namespace DumpDebugger.Host;

/// <summary>
/// Command line: <c>DumpDebugger.Host.exe [dump.dmp] [--cdp &lt;port&gt;]</c>.
/// <c>DUMPDEBUGGER_WEB_URL</c> points the WebView at a Vite dev server instead of the bundled
/// assets (hot reload). <c>--cdp</c> exposes the Chrome DevTools Protocol on localhost so
/// <c>web/scripts/shot.mjs</c> can drive and screenshot the real app.
/// </summary>
public sealed record HostOptions(string? StartupDumpPath, int? CdpPort, string? DevServerUrl)
{
    public static HostOptions FromCommandLine(string[] args)
    {
        string? dump = null;
        int? cdp = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--cdp" && i + 1 < args.Length && int.TryParse(args[i + 1], out var port))
            {
                cdp = port;
                i++;
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                dump = Path.GetFullPath(args[i]);
            }
        }

        return new HostOptions(dump, cdp, Environment.GetEnvironmentVariable("DUMPDEBUGGER_WEB_URL"));
    }
}
