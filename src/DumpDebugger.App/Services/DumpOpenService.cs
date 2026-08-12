using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Ipc;

namespace DumpDebugger_App.Services;

/// <summary>
/// End-to-end "open a dump" flow: sniff architecture, launch the matching worker, connect,
/// request metadata, and clean up the worker process afterward.
/// </summary>
public static class DumpOpenService
{
    public static async Task<DumpMetadata> OpenAsync(
        string dumpPath,
        IProgress<ProgressNotification>? progress,
        CancellationToken ct = default)
    {
        progress?.Report(new ProgressNotification("Detecting architecture", 0.0, dumpPath));
        var architecture = MinidumpArchitectureSniffer.Sniff(dumpPath);

        var pipeName = $"DumpDebugger-{Guid.NewGuid():N}";
        progress?.Report(new ProgressNotification("Starting worker", 0.05, $"{architecture} worker"));
        using var workerProcess = WorkerLauncher.Launch(architecture, pipeName);

        try
        {
            await using var client = await WorkerClient.ConnectAsync(pipeName, ct).ConfigureAwait(false);
            return await client.OpenDumpAsync(dumpPath, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!workerProcess.HasExited)
            {
                workerProcess.Kill(entireProcessTree: true);
            }
        }
    }
}
