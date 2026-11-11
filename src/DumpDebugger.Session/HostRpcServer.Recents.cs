using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;

namespace DumpDebugger.Session;

public sealed partial class HostRpcServer
{
    private readonly RecentsStore _recents = new();

    public RecentsStore Recents => _recents;

    private partial Task OnDumpOpenedAsync(DumpMetadata metadata, Workspace workspace, Diagnosis? diagnosis)
    {
        _recents.Touch(metadata, diagnosis);
        return Task.CompletedTask;
    }

    private partial Task OnAnalysisCompletedAsync(Workspace workspace, Diagnosis diagnosis)
    {
        _recents.UpdateVerdict(workspace.DumpPath, diagnosis);
        return Task.CompletedTask;
    }
}

/// <summary>Recently opened dumps with their last verdict badge, in %LOCALAPPDATA%\DumpDebugger\recents.json.</summary>
public sealed class RecentsStore
{
    private const int Max = 20;
    private readonly object _lock = new();

    private static string FilePath => Path.Combine(AppPaths.Ensure(""), "recents.json");

    public IReadOnlyList<RecentDump> List()
    {
        lock (_lock)
        {
            return Load().Select(r => r with { Exists = File.Exists(r.Path) }).ToList();
        }
    }

    public void Touch(DumpMetadata metadata, Diagnosis? diagnosis)
    {
        lock (_lock)
        {
            var items = Load();
            var existing = items.FirstOrDefault(r => SamePath(r.Path, metadata.DumpPath));
            items.RemoveAll(r => SamePath(r.Path, metadata.DumpPath));
            var rt = metadata.Runtimes.FirstOrDefault();
            var badge = diagnosis is not null ? Badge(diagnosis) : (existing?.VerdictSeverity, existing?.VerdictLabel);
            items.Insert(0, new RecentDump(
                Path.GetFullPath(metadata.DumpPath),
                Path.GetFileName(metadata.DumpPath),
                Path.GetDirectoryName(Path.GetFullPath(metadata.DumpPath)) ?? "",
                DateTimeOffset.UtcNow,
                rt is null ? null : $"{(rt.Family == RuntimeFamily.NetFramework ? ".NET Fx" : ".NET")} {ShortVersion(rt.ProductVersion)}",
                metadata.Architecture.ToString().ToLowerInvariant(),
                metadata.FileSizeBytes,
                badge.Item1,
                badge.Item2,
                true,
                IsSample(metadata.DumpPath)));
            Save(items.Take(Max).ToList());
        }
    }

    public void UpdateVerdict(string path, Diagnosis diagnosis)
    {
        lock (_lock)
        {
            var items = Load();
            var i = items.FindIndex(r => SamePath(r.Path, path));
            if (i >= 0)
            {
                var (severity, label) = Badge(diagnosis);
                items[i] = items[i] with { VerdictSeverity = severity, VerdictLabel = label };
                Save(items);
            }
        }
    }

    public void Remove(string path)
    {
        lock (_lock)
        {
            var items = Load();
            items.RemoveAll(r => SamePath(r.Path, path));
            Save(items);
        }
    }

    /// <summary>The short badge on Home: "Deadlock", "Heap 3.1 GB", "Healthy".</summary>
    public static (Severity, string) Badge(Diagnosis d)
    {
        var top = d.Findings.FirstOrDefault();
        if (top is null)
        {
            return (Severity.Ok, "Healthy");
        }

        // Nothing urgent, but a memory note is still what someone would want to see in the list.
        if (d.Verdict.Severity == Severity.Ok)
        {
            return top.Analyzer == "heap-dominance" ? (Severity.Info, "Heap " + (d.Vitals.FirstOrDefault(v => v.Key == "heap")?.Value ?? "")) : (Severity.Ok, "Healthy");
        }

        var label = top.Analyzer switch
        {
            "deadlock" => "Deadlock",
            "lock-contention" => "Lock convoy",
            "threadpool-starvation" => "Thread-pool starvation",
            "sync-over-async" => "Sync-over-async",
            "exceptions" => "Exceptions",
            "finalizer" => "Finalizer backlog",
            "loh-fragmentation" => "LOH fragmentation",
            "http-sockets" => "Socket exhaustion",
            "timers" => "Timer leak",
            "heap-dominance" => "Heap " + (d.Vitals.FirstOrDefault(v => v.Key == "heap")?.Value ?? ""),
            _ => top.Title.Replace("`", "") is { Length: > 28 } t ? t[..27] + "…" : top.Title.Replace("`", ""),
        };
        return (top.Severity, label.Trim());
    }

    private static string ShortVersion(string v)
    {
        var parts = v.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : v;
    }

    private static bool IsSample(string path) =>
        Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "").Equals("Samples", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).StartsWith("sample-", StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static List<RecentDump> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? Core.Rpc.ContractJson.Deserialize<List<RecentDump>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static void Save(List<RecentDump> items) =>
        File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(items, Core.Rpc.ContractJson.Indented));
}
