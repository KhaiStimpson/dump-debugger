using System.IO.Pipes;
using DumpDebugger.Core.Rpc;
using DumpDebugger.Worker;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: DumpDebugger.Worker <pipeName>");
    return 1;
}

var pipeName = args[0];

await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
await pipe.WaitForConnectionAsync();

// One dump per worker process: it stays loaded for the life of the pipe connection so every
// query after "dump.open" reuses it. The host kills the process when the dump is closed.
using var handlers = new WorkerHandlers();
var connection = new RpcConnection(pipe, handlers.HandleAsync);
connection.Start();
await connection.Completion;
return 0;
