using System.Diagnostics;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Findings;
using DumpDebugger.Core.Ipc;

namespace DumpDebugger_App.Services;

/// <summary>
/// Owns one worker process + pipe connection for the lifetime of an open dump, so later
/// requests (threads now, memory/locks in later phases) reuse the already-loaded dump
/// instead of paying the load cost again. Dispose to kill the worker and close the pipe.
/// </summary>
public sealed class WorkerSession : IAsyncDisposable
{
    private readonly Process _workerProcess;
    private readonly WorkerClient _client;

    private WorkerSession(Process workerProcess, WorkerClient client)
    {
        _workerProcess = workerProcess;
        _client = client;
    }

    public static async Task<(WorkerSession Session, DumpMetadata Metadata)> OpenAsync(
        string dumpPath,
        IProgress<ProgressNotification>? progress,
        CancellationToken ct = default)
    {
        progress?.Report(new ProgressNotification("Detecting architecture", 0.0, dumpPath));
        var architecture = MinidumpArchitectureSniffer.Sniff(dumpPath);

        var pipeName = $"DumpDebugger-{Guid.NewGuid():N}";
        progress?.Report(new ProgressNotification("Starting worker", 0.05, $"{architecture} worker"));
        var workerProcess = WorkerLauncher.Launch(architecture, pipeName);

        try
        {
            var client = await WorkerClient.ConnectAsync(pipeName, ct).ConfigureAwait(false);
            var metadata = await client.OpenDumpAsync(dumpPath, progress, ct).ConfigureAwait(false);
            return (new WorkerSession(workerProcess, client), metadata);
        }
        catch
        {
            if (!workerProcess.HasExited)
            {
                workerProcess.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default) =>
        _client.GetThreadsAsync(ct);

    public Task<GetLocksResponse> GetLocksAsync(CancellationToken ct = default) =>
        _client.GetLocksAsync(ct);

    public Task<GetMemoryResponse> GetMemoryAsync(CancellationToken ct = default) =>
        _client.GetMemoryAsync(ct);

    public Task<FindingsDocument> GetFindingsAsync(CancellationToken ct = default) =>
        _client.GetFindingsAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
        if (!_workerProcess.HasExited)
        {
            _workerProcess.Kill(entireProcessTree: true);
        }

        _workerProcess.Dispose();
    }
}
