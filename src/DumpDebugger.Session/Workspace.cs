using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

/// <summary>
/// The sidecar folder for one dump (PLAN.md §3.1): <c>&lt;dump&gt;.dumpdebugger/</c> next to the
/// .dmp, or under %LOCALAPPDATA% when the dump's folder isn't writable. It caches the
/// diagnosis (so reopening is instant) and, from R6/R7, pins, notes and the heap snapshot.
/// Cached analysis is only reused when the dump file, the analysis engine and the loaded runtimes are unchanged.
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

    /// <summary>
    /// The cached diagnosis, valid only for the same dump file, engine and runtime identity
    /// (<see cref="DumpMetadataExtensions.RuntimeIdentity"/>): a newly loadable runtime or a
    /// different primary runtime means the cached result describes other data.
    /// </summary>
    public Diagnosis? TryLoadDiagnosis(string runtimeIdentity)
    {
        try
        {
            if (Read<WorkspaceManifest>(ManifestFile) != CurrentManifest(runtimeIdentity))
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

    public void SaveDiagnosis(Diagnosis diagnosis, string runtimeIdentity)
    {
        Write(DiagnosisFile, diagnosis with { FromCache = false });
        Write(ManifestFile, CurrentManifest(runtimeIdentity));
    }

    private const string SnapshotFile = "snapshot.json";
    private const string SnapshotManifestFile = "snapshot.manifest.json";

    /// <summary>
    /// The compare snapshot, valid for the same dump file and engine as when it was built, and,
    /// when <paramref name="runtimeIdentity"/> is given, the same runtime identity. Compare
    /// candidates other than the open dump pass null: their snapshot only has to be self-consistent.
    /// </summary>
    public HeapSnapshot? TryLoadSnapshot(string? runtimeIdentity = null)
    {
        try
        {
            var manifest = Read<WorkspaceManifest>(SnapshotManifestFile);
            if (manifest is null || manifest != CurrentManifest(runtimeIdentity ?? manifest.RuntimeIdentity))
            {
                return null;
            }

            return Read<HeapSnapshot>(SnapshotFile) is { SchemaVersion: HeapSnapshot.CurrentSchemaVersion } s ? s : null;
        }
        catch
        {
            return null;
        }
    }

    public bool HasSnapshot => HasSnapshotFor(null);

    public bool HasSnapshotFor(string? runtimeIdentity) => File.Exists(PathOf(SnapshotFile)) && TryLoadSnapshot(runtimeIdentity) is not null;

    public void SaveSnapshot(HeapSnapshot snapshot, string runtimeIdentity)
    {
        Write(SnapshotFile, snapshot);
        Write(SnapshotManifestFile, CurrentManifest(runtimeIdentity));
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

    private WorkspaceManifest CurrentManifest(string? runtimeIdentity)
    {
        var info = new FileInfo(DumpPath);
        return new WorkspaceManifest(Engine.Version, info.Length, info.LastWriteTimeUtc.Ticks, runtimeIdentity);
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

    private sealed record WorkspaceManifest(string EngineVersion, long DumpSize, long DumpWriteTicks, string? RuntimeIdentity);
}
