using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

/// <summary>
/// One architecture-matched worker process plus its pipe connection, for the lifetime of an
/// open dump (PLAN.md §2.2: ClrMD must run in a process matching the dump's bitness, so the
/// architecture is sniffed from the minidump header before anything is launched).
/// </summary>
public sealed class WorkerSession : IAsyncDisposable
{
    private readonly Process _process;

    private WorkerSession(Process process, RpcConnection connection, DumpArchitecture architecture)
    {
        _process = process;
        Connection = connection;
        Architecture = architecture;
    }

    public RpcConnection Connection { get; }

    public DumpArchitecture Architecture { get; }

    public int ProcessId => _process.Id;

    public DumpMetadata? Metadata { get; private set; }

    public bool HasExited => _process.HasExited;

    public static async Task<WorkerSession> StartAsync(
        string dumpPath,
        Action<string, JsonElement?> onNotification,
        CancellationToken ct = default)
    {
        var architecture = MinidumpArchitectureSniffer.Sniff(dumpPath);
        var pipeName = $"DumpDebugger-{Guid.NewGuid():N}";
        var process = WorkerLauncher.Launch(architecture, pipeName);

        try
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15_000, ct).ConfigureAwait(false);
            var connection = new RpcConnection(pipe);
            connection.NotificationReceived += onNotification;
            connection.Start();
            return new WorkerSession(process, connection, architecture);
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }
    }

    public async Task<DumpMetadata> OpenDumpAsync(string dumpPath, CancellationToken ct = default)
    {
        Metadata = await Connection.CallAsync<DumpMetadata>("dump.open", new OpenDumpParams(dumpPath), ct).ConfigureAwait(false)
            ?? throw new IOException("Worker returned no metadata for dump.open.");
        return Metadata;
    }

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync().ConfigureAwait(false);
        TryKill(_process);
        _process.Dispose();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
