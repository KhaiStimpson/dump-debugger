using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

public sealed partial class HostRpcServer
{
    private Task? _snapshotTask;

    /// <summary>After analysis, capture the compare snapshot in the background (once per dump and engine version).</summary>
    private void EnsureSnapshotInBackground(WorkerSession session, Workspace workspace, Diagnosis diagnosis)
    {
        if (workspace.HasSnapshot)
        {
            return;
        }

        _snapshotTask = Task.Run(async () =>
        {
            try
            {
                var snapshot = await session.Connection.CallAsync<HeapSnapshot>("snapshot.build", new SnapshotParams(diagnosis)).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    workspace.SaveSnapshot(snapshot);
                    await NotifyWebAsync("snapshot.ready", new PathParams(workspace.DumpPath)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is RpcException or IOException or OperationCanceledException)
            {
                // The dump was closed mid-way; the next open tries again.
            }
        });
    }

    public Task? SnapshotTask => _snapshotTask;

    private IReadOnlyList<CompareCandidate> CompareCandidates() =>
        _recents.List()
            .Select(r => new CompareCandidate(r.Path, r.FileName, r.Folder, r.Exists && Workspace.For(r.Path).HasSnapshot, r.Exists, r.OpenedAt, r.Runtime, r.VerdictSeverity, r.VerdictLabel))
            .ToList();

    /// <summary>
    /// Compares two dumps from their cached snapshots. B defaults to the open dump. A dump that
    /// has never been analyzed is analyzed once by a temporary worker and its snapshot cached,
    /// so from then on it compares without being reopened.
    /// </summary>
    private async Task<CompareResult> CompareAsync(CompareParams p, CancellationToken ct)
    {
        var open = _workspace?.DumpPath;
        var currentPath = p.CurrentPath ?? open
            ?? throw new RpcException(new RpcError(RpcError.InvalidParams, "Pick the current dump (B), or open one first."));

        var a = await LoadOrBuildSnapshotAsync(p.BaselinePath, ct).ConfigureAwait(false);
        var b = await LoadOrBuildSnapshotAsync(currentPath, ct).ConfigureAwait(false);
        return CompareEngine.Compare(a, b, bIsOpen: SamePath(currentPath, open), aIsOpen: SamePath(p.BaselinePath, open));
    }

    private async Task<HeapSnapshot> LoadOrBuildSnapshotAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new RpcException(new RpcError(RpcError.InvalidParams, $"File not found: {path}"));
        }

        var workspace = Workspace.For(path);
        if (workspace.TryLoadSnapshot() is { } cached)
        {
            return cached;
        }

        // The open dump: its snapshot is being built (or will be once analysis finishes).
        if (SamePath(path, _workspace?.DumpPath) && _session is { } session)
        {
            if (_analysisTask is { } analysis)
            {
                await analysis.WaitAsync(ct).ConfigureAwait(false);
            }

            if (_snapshotTask is { } snap)
            {
                await snap.WaitAsync(ct).ConfigureAwait(false);
            }

            if (workspace.TryLoadSnapshot() is { } built)
            {
                return built;
            }

            var diagnosis = _latest ?? throw new RpcException(new RpcError(RpcError.InternalError, "The open dump hasn't been analyzed yet."));
            var fresh = await session.Connection.CallAsync<HeapSnapshot>("snapshot.build", new SnapshotParams(diagnosis), ct).ConfigureAwait(false);
            workspace.SaveSnapshot(fresh!);
            return fresh!;
        }

        // Any other dump: analyze it once with a temporary worker, cache diagnosis + snapshot.
        await NotifyWebAsync("dump.progress", new ProgressNotification($"Analyzing {Path.GetFileName(path)} for comparison (once)", 0.1, path)).ConfigureAwait(false);
        await using var temp = await WorkerSession.StartAsync(path, (_, _) => { }, ct).ConfigureAwait(false);
        await temp.OpenDumpAsync(path, ct).ConfigureAwait(false);
        var diag = workspace.TryLoadDiagnosis()
                   ?? await temp.Connection.CallAsync<Diagnosis>("analysis.run", new AnalysisRunParams(), ct).ConfigureAwait(false)
                   ?? throw new RpcException(new RpcError(RpcError.InternalError, "Analysis returned nothing."));
        workspace.SaveDiagnosis(diag);
        var snapshot = await temp.Connection.CallAsync<HeapSnapshot>("snapshot.build", new SnapshotParams(diag), ct).ConfigureAwait(false)
                       ?? throw new RpcException(new RpcError(RpcError.InternalError, "Snapshot returned nothing."));
        workspace.SaveSnapshot(snapshot);
        return snapshot;
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
