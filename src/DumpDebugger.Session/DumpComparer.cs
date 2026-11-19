using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

/// <summary>
/// Compares two dumps from their snapshots. A dump that is open contributes its own snapshot;
/// one that isn't uses its cached snapshot, or is analyzed once by a temporary worker and its
/// snapshot cached, so from then on it compares without being reopened.
/// </summary>
public static class DumpComparer
{
    public static async Task<CompareResult> CompareAsync(
        string baselinePath,
        string currentPath,
        IReadOnlyList<DumpSession> open,
        Func<string, Task>? onAnalyzing,
        CancellationToken ct)
    {
        var a = await LoadOrBuildSnapshotAsync(baselinePath, open, onAnalyzing, ct).ConfigureAwait(false);
        var b = await LoadOrBuildSnapshotAsync(currentPath, open, onAnalyzing, ct).ConfigureAwait(false);
        return CompareEngine.Compare(a, b, bIsOpen: open.Any(s => s.IsFor(currentPath)), aIsOpen: open.Any(s => s.IsFor(baselinePath)));
    }

    private static async Task<HeapSnapshot> LoadOrBuildSnapshotAsync(string path, IReadOnlyList<DumpSession> open, Func<string, Task>? onAnalyzing, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new RpcException(new RpcError(RpcError.InvalidParams, $"File not found: {path}"));
        }

        if (open.FirstOrDefault(s => s.IsFor(path)) is { } session)
        {
            return await session.GetSnapshotAsync(ct).ConfigureAwait(false);
        }

        var workspace = Workspace.For(path);
        if (workspace.TryLoadSnapshot() is { } cached)
        {
            return cached;
        }

        // Not open and never snapshotted: analyze it once with a temporary worker, cache diagnosis + snapshot.
        if (onAnalyzing is not null)
        {
            await onAnalyzing(path).ConfigureAwait(false);
        }

        await using var temp = await WorkerSession.StartAsync(path, (_, _) => { }, ct).ConfigureAwait(false);
        var identity = (await temp.OpenDumpAsync(path, ct).ConfigureAwait(false)).RuntimeIdentity();
        var diag = workspace.TryLoadDiagnosis(identity)
                   ?? await temp.Connection.CallAsync<Diagnosis>("analysis.run", new AnalysisRunParams(), ct).ConfigureAwait(false)
                   ?? throw new RpcException(new RpcError(RpcError.InternalError, "Analysis returned nothing."));
        workspace.SaveDiagnosis(diag, identity);
        var snapshot = await temp.Connection.CallAsync<HeapSnapshot>("snapshot.build", new SnapshotParams(diag), ct).ConfigureAwait(false)
                       ?? throw new RpcException(new RpcError(RpcError.InternalError, "Snapshot returned nothing."));
        workspace.SaveSnapshot(snapshot, identity);
        return snapshot;
    }
}
