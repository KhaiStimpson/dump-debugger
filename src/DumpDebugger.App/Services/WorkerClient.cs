using System.IO.Pipes;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Ipc;

namespace DumpDebugger_App.Services;

/// <summary>
/// Client side of the named-pipe IPC contract (PLAN.md §6): connects to an already-launched
/// worker, sends an OpenDumpRequest, and surfaces progress notifications until the final
/// OpenDumpResponse or ErrorResponse arrives.
/// </summary>
public sealed class WorkerClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;

    private WorkerClient(NamedPipeClientStream pipe) => _pipe = pipe;

    public static async Task<WorkerClient> ConnectAsync(string pipeName, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000, ct).ConfigureAwait(false);
        return new WorkerClient(pipe);
    }

    public async Task<DumpMetadata> OpenDumpAsync(
        string dumpPath,
        IProgress<ProgressNotification>? progress,
        CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await IpcFrame.WriteAsync(
            _pipe,
            new IpcEnvelope(IpcMessageKind.OpenDumpRequest, requestId, IpcFrame.SerializePayload(new OpenDumpRequest(dumpPath))),
            ct).ConfigureAwait(false);

        while (true)
        {
            var envelope = await IpcFrame.ReadAsync(_pipe, ct).ConfigureAwait(false)
                ?? throw new IOException("Worker closed the pipe before responding.");

            switch (envelope.Kind)
            {
                case IpcMessageKind.ProgressNotification:
                    var note = IpcFrame.DeserializePayload<ProgressNotification>(envelope);
                    if (note is not null)
                    {
                        progress?.Report(note);
                    }

                    break;

                case IpcMessageKind.OpenDumpResponse:
                    var response = IpcFrame.DeserializePayload<OpenDumpResponse>(envelope);
                    return response?.Metadata ?? throw new IOException("Worker sent an empty OpenDumpResponse.");

                case IpcMessageKind.ErrorResponse:
                    var error = IpcFrame.DeserializePayload<ErrorResponse>(envelope);
                    throw new InvalidOperationException(error?.Message ?? "Worker reported an unknown error.");
            }
        }
    }

    public async Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await IpcFrame.WriteAsync(
            _pipe,
            new IpcEnvelope(IpcMessageKind.GetThreadsRequest, requestId, IpcFrame.SerializePayload(new GetThreadsRequest())),
            ct).ConfigureAwait(false);

        while (true)
        {
            var envelope = await IpcFrame.ReadAsync(_pipe, ct).ConfigureAwait(false)
                ?? throw new IOException("Worker closed the pipe before responding.");

            switch (envelope.Kind)
            {
                case IpcMessageKind.GetThreadsResponse:
                    var response = IpcFrame.DeserializePayload<GetThreadsResponse>(envelope);
                    return response?.Threads ?? [];

                case IpcMessageKind.ErrorResponse:
                    var error = IpcFrame.DeserializePayload<ErrorResponse>(envelope);
                    throw new InvalidOperationException(error?.Message ?? "Worker reported an unknown error.");
            }
        }
    }

    public async Task<GetLocksResponse> GetLocksAsync(CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await IpcFrame.WriteAsync(
            _pipe,
            new IpcEnvelope(IpcMessageKind.GetLocksRequest, requestId, IpcFrame.SerializePayload(new GetLocksRequest())),
            ct).ConfigureAwait(false);

        while (true)
        {
            var envelope = await IpcFrame.ReadAsync(_pipe, ct).ConfigureAwait(false)
                ?? throw new IOException("Worker closed the pipe before responding.");

            switch (envelope.Kind)
            {
                case IpcMessageKind.GetLocksResponse:
                    var response = IpcFrame.DeserializePayload<GetLocksResponse>(envelope);
                    return response ?? new GetLocksResponse([], []);

                case IpcMessageKind.ErrorResponse:
                    var error = IpcFrame.DeserializePayload<ErrorResponse>(envelope);
                    throw new InvalidOperationException(error?.Message ?? "Worker reported an unknown error.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
