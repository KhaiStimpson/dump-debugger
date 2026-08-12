using System.Text.Json;
using DumpDebugger.Core.Findings;

namespace DumpDebugger_App.Services;

/// <summary>
/// PLAN.md §3.1 "Workspace concept": opening a dump creates a workspace folder — sidecar to
/// the .dmp — holding the analysis findings (and, in later iterations, annotations and
/// bookmarks). Reopening the same dump is instant because the findings don't need
/// recomputing. Named "&lt;dump-file-name&gt;.workspace" next to the dump itself.
/// </summary>
public static class WorkspaceService
{
    private const string FindingsFileName = "findings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string GetWorkspaceDirectory(string dumpPath) => dumpPath + ".workspace";

    public static FindingsDocument? TryLoadCachedFindings(string dumpPath)
    {
        var path = Path.Combine(GetWorkspaceDirectory(dumpPath), FindingsFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FindingsDocument>(json, JsonOptions);
        }
        catch
        {
            return null; // Corrupt or stale cache; caller falls back to recomputing.
        }
    }

    public static void SaveFindings(string dumpPath, FindingsDocument document)
    {
        var dir = GetWorkspaceDirectory(dumpPath);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(Path.Combine(dir, FindingsFileName), json);
    }
}
