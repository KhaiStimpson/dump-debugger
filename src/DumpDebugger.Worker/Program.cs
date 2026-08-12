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

while (pipe.IsConnected)
{
    var envelope = await IpcFrame.ReadAsync(pipe);
    if (envelope is null)
    {
        break;
    }

    if (envelope.Kind != IpcMessageKind.OpenDumpRequest)
    {
        continue;
    }

    var request = IpcFrame.DeserializePayload<OpenDumpRequest>(envelope);
    if (request is null)
    {
        continue;
    }

    await IpcFrame.WriteAsync(pipe, new IpcEnvelope(
        IpcMessageKind.ProgressNotification,
        envelope.RequestId,
        IpcFrame.SerializePayload(new ProgressNotification("Loading dump", 0.1, request.DumpPath))));

    try
    {
        using var loaded = DumpLoader.Load(request.DumpPath, allowSymbolServer: false);

        await IpcFrame.WriteAsync(pipe, new IpcEnvelope(
            IpcMessageKind.OpenDumpResponse,
            envelope.RequestId,
            IpcFrame.SerializePayload(new OpenDumpResponse(loaded.Metadata))));
    }
    catch (Exception ex)
    {
        await IpcFrame.WriteAsync(pipe, new IpcEnvelope(
            IpcMessageKind.ErrorResponse,
            envelope.RequestId,
            IpcFrame.SerializePayload(new ErrorResponse(ex.Message, ex.ToString()))));
    }
}

return 0;
