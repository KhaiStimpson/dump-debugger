using System.Text.Json;
using DumpDebugger.Analysis;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Worker;

/// <summary>
/// Routes worker-side RPC methods to the analysis layer. ClrMD reads are serialized through
/// a single gate so concurrent requests (a cancel arriving mid-walk, a thread query while the
/// heap is being walked) can't corrupt shared reader state.
/// </summary>
public sealed class WorkerHandlers : IDisposable
{
    private readonly SemaphoreSlim _clrGate = new(1, 1);
    private LoadedDump? _loaded;

    public async Task<JsonElement?> HandleAsync(string method, JsonElement? @params, RpcCallContext context)
    {
        switch (method)
        {
            case "dump.open":
                {
                    var request = ContractJson.FromElement<OpenDumpParams>(@params)
                        ?? throw new RpcException(new RpcError(RpcError.InvalidParams, "dump.open needs a path."));
                    await context.NotifyAsync("dump.progress", new ProgressNotification("Loading dump", 0.1, request.Path));
                    var loaded = await WithClrAsync(() => DumpLoader.Load(request.Path, allowSymbolServer: false), context.Cancellation);
                    _loaded?.Dispose();
                    _loaded = loaded;
                    return ContractJson.ToElement(loaded.Metadata);
                }

            case "threads.list":
                {
                    var runtime = RequireRuntime();
                    var threads = await WithClrAsync(() => ThreadEnumerator.Enumerate(runtime), context.Cancellation);
                    return ContractJson.ToElement(threads);
                }

            default:
                throw new RpcException(new RpcError(RpcError.MethodNotFound, $"Worker has no method '{method}'."));
        }
    }

    private Microsoft.Diagnostics.Runtime.ClrRuntime RequireRuntime()
    {
        if (_loaded is null)
        {
            throw new RpcException(new RpcError(RpcError.NoDumpOpen, "No dump is open."));
        }

        if (_loaded.Runtimes.Count == 0)
        {
            throw new RpcException(new RpcError(RpcError.NoDumpOpen, "The dump has no .NET runtime that could be loaded (DAC not found)."));
        }

        return _loaded.Runtimes[0];
    }

    private async Task<T> WithClrAsync<T>(Func<T> work, CancellationToken ct)
    {
        await _clrGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(work, ct).ConfigureAwait(false);
        }
        finally
        {
            _clrGate.Release();
        }
    }

    public void Dispose() => _loaded?.Dispose();
}
