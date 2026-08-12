using System.IO.Pipes;
using DumpDebugger.Analysis;
using DumpDebugger.Core.Ipc;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: DumpDebugger.Worker <pipeName>");
    return 1;
}

var pipeName = args[0];

using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
await pipe.WaitForConnectionAsync();

// The dump stays loaded for the life of the pipe connection so later requests (threads,
// memory, etc. in later phases) don't have to re-open it. Disposed when the pipe closes.
LoadedDump? loaded = null;

try
{
    while (pipe.IsConnected)
    {
        var envelope = await IpcFrame.ReadAsync(pipe);
        if (envelope is null)
        {
            break;
        }

        try
        {
            switch (envelope.Kind)
            {
                case IpcMessageKind.OpenDumpRequest:
                    await HandleOpenDumpAsync(pipe, envelope, v => loaded = v);
                    break;

                case IpcMessageKind.GetThreadsRequest:
                    await HandleGetThreadsAsync(pipe, envelope, loaded);
                    break;
            }
        }
        catch (Exception ex)
        {
            await IpcFrame.WriteAsync(pipe, new IpcEnvelope(
                IpcMessageKind.ErrorResponse,
                envelope.RequestId,
                IpcFrame.SerializePayload(new ErrorResponse(ex.Message, ex.ToString()))));
        }
    }
}
finally
{
    loaded?.Dispose();
}

return 0;

static async Task HandleOpenDumpAsync(NamedPipeServerStream pipe, IpcEnvelope envelope, Action<LoadedDump> onLoaded)
{
    var request = IpcFrame.DeserializePayload<OpenDumpRequest>(envelope);
    if (request is null)
    {
        return;
    }

    await IpcFrame.WriteAsync(pipe, new IpcEnvelope(
        IpcMessageKind.ProgressNotification,
        envelope.RequestId,
        IpcFrame.SerializePayload(new ProgressNotification("Loading dump", 0.1, request.DumpPath))));

    var loaded = DumpLoader.Load(request.DumpPath, allowSymbolServer: false);
    onLoaded(loaded);

    await IpcFrame.WriteAsync(pipe, new IpcEnvelope(
        IpcMessageKind.OpenDumpResponse,
        envelope.RequestId,
        IpcFrame.SerializePayload(new OpenDumpResponse(loaded.Metadata))));
}

static async Task HandleGetThreadsAsync(NamedPipeServerStream pipe, IpcEnvelope envelope, LoadedDump? loaded)
{
    if (loaded is null || loaded.Runtimes.Count == 0)
    {
        await IpcFrame.WriteAsync(pipe, new IpcEnvelope(
            IpcMessageKind.GetThreadsResponse,
            envelope.RequestId,
            IpcFrame.SerializePayload(new GetThreadsResponse([]))));
        return;
    }

    var threads = ThreadEnumerator.Enumerate(loaded.Runtimes[0]);

    await IpcFrame.WriteAsync(pipe, new IpcEnvelope(
        IpcMessageKind.GetThreadsResponse,
        envelope.RequestId,
        IpcFrame.SerializePayload(new GetThreadsResponse(threads))));
}
