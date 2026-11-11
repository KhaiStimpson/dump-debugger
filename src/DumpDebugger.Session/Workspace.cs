using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

/// <summary>
/// The sidecar folder for one dump (PLAN.md §3.1): <c>&lt;dump&gt;.dumpdebugger/</c> next to the
/// .dmp, or under %LOCALAPPDATA% when the dump's folder isn't writable. It caches the
/// diagnosis (so reopening is instant) and, from R6/R7, pins, notes and the heap snapshot.
/// Cached analysis is only reused when the dump file and the analysis engine are unchanged.
/// </summary>
public sealed class Workspace
{
    private const string ManifestFile = "workspace.json";
    private const string DiagnosisFile = "diagnosis.json";

    private Workspace(string dumpPath, string directory)
    {
        DumpPath = dumpPath;
        Directory = directory;
    }

    public string DumpPath { get; }

    public string Directory { get; }

    public static Workspace For(string dumpPath)
    {
        var full = Path.GetFullPath(dumpPath);
        var beside = full + ".dumpdebugger";
        if (TryEnsureWritable(beside))
        {
            return new Workspace(full, beside);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..16];
        var fallback = Path.Combine(AppPaths.Ensure("Workspaces"), $"{Path.GetFileName(full)}-{hash}");
        System.IO.Directory.CreateDirectory(fallback);
        return new Workspace(full, fallback);
    }

    public string PathOf(string file) => Path.Combine(Directory, file);

    public Diagnosis? TryLoadDiagnosis()
    {
        try
        {
            var manifest = Read<WorkspaceManifest>(ManifestFile);
            if (manifest is null || manifest != CurrentManifest())
            {
                return null;
            }

            var diagnosis = Read<Diagnosis>(DiagnosisFile);
            return diagnosis is { SchemaVersion: Diagnosis.CurrentSchemaVersion, Complete: true } ? diagnosis : null;
        }
        catch
        {
            return null; // corrupt or stale cache: recompute
        }
    }

    public void SaveDiagnosis(Diagnosis diagnosis)
    {
        Write(DiagnosisFile, diagnosis with { FromCache = false });
        Write(ManifestFile, CurrentManifest());
    }

    public T? Read<T>(string file)
    {
        var path = PathOf(file);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), ContractJson.Options) : default;
    }

    public void Write<T>(string file, T value)
    {
        var path = PathOf(file);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, ContractJson.Indented));
        File.Move(temp, path, overwrite: true);
    }

    private WorkspaceManifest CurrentManifest()
    {
        var info = new FileInfo(DumpPath);
        return new WorkspaceManifest(Engine.Version, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static bool TryEnsureWritable(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record WorkspaceManifest(string EngineVersion, long DumpSize, long DumpWriteTicks);
}
