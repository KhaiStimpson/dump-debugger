using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

public sealed partial class HostRpcServer
{
    public Task? SnapshotTask => _dump?.SnapshotTask;

    private IReadOnlyList<CompareCandidate> CompareCandidates() =>
        _recents.List()
            .Select(r => new CompareCandidate(r.Path, r.FileName, r.Folder, r.Exists && Workspace.For(r.Path).HasSnapshot, r.Exists, r.OpenedAt, r.Runtime, r.VerdictSeverity, r.VerdictLabel))
            .ToList();

    /// <summary>Compares two dumps from their snapshots (see <see cref="DumpComparer"/>). B defaults to the open dump.</summary>
    private Task<CompareResult> CompareAsync(CompareParams p, CancellationToken ct)
    {
        var dump = _dump;
        var currentPath = p.CurrentPath ?? dump?.DumpPath
            ?? throw new RpcException(new RpcError(RpcError.InvalidParams, "Pick the current dump (B), or open one first."));
        return DumpComparer.CompareAsync(
            p.BaselinePath,
            currentPath,
            dump is null ? [] : [dump],
            path => NotifyWebAsync("dump.progress", new ProgressNotification($"Analyzing {Path.GetFileName(path)} for comparison (once)", 0.1, path)),
            ct);
    }
}
