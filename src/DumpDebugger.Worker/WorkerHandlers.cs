using System.Text.Json;
using DumpDebugger.Analysis;
using DumpDebugger.Analysis.Locks;
using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Worker;

/// <summary>
/// Worker-side RPC methods (protocol v2). Long work (<c>analysis.run</c>) streams
/// <c>analysis.updated</c> snapshots and honours per-request cancellation; queries return
/// aggregated, paged results, never raw heap contents.
/// </summary>
public sealed class WorkerHandlers : IDisposable
{
    private DumpIndex? _index;

    public async Task<JsonElement?> HandleAsync(string method, JsonElement? @params, RpcCallContext context)
    {
        var ct = context.Cancellation;
        switch (method)
        {
            case "dump.open":
                {
                    var request = Params<OpenDumpParams>(@params);
                    await context.NotifyAsync("dump.progress", new ProgressNotification("Loading dump", 0.1, request.Path));
                    var index = await Task.Run(() => DumpIndex.Open(request.Path), ct);
                    _index?.Dispose();
                    _index = index;
                    return ContractJson.ToElement(index.Loaded.Metadata);
                }

            case "analysis.run":
                {
                    var p = ContractJson.FromElement<AnalysisRunParams>(@params) ?? new AnalysisRunParams();
                    var index = RequireRuntime();
                    var pipeline = new AnalysisPipeline(index, d => context.NotifyAsync("analysis.updated", d));
                    return ContractJson.ToElement(await pipeline.RunAsync(p.IncludeHeap, ct));
                }

            case "threads.groups":
                {
                    var q = Params<ThreadGroupsQuery>(@params);
                    var (threads, _) = await RequireRuntime().EnsureThreadsAsync(ct);
                    return ContractJson.ToElement(ThreadQueries.Groups(threads, q));
                }

            case "threads.get":
                {
                    var q = Params<ThreadQuery>(@params);
                    var (threads, locks) = await RequireRuntime().EnsureThreadsAsync(ct);
                    var thread = threads.FirstOrDefault(t => t.Id == q.Id)
                        ?? throw new RpcException(new RpcError(RpcError.InvalidParams, $"No thread #{q.Id} in this dump."));
                    return ContractJson.ToElement(ThreadQueries.Detail(thread, threads, locks));
                }

            case "locks.graph":
                {
                    var q = ContractJson.FromElement<WaitGraphQuery>(@params) ?? new WaitGraphQuery(false);
                    var (threads, locks) = await RequireRuntime().EnsureThreadsAsync(ct);
                    return ContractJson.ToElement(LockModelBuilder.ToGraph(locks, threads, q.CyclesOnly));
                }

            default:
                throw new RpcException(new RpcError(RpcError.MethodNotFound, $"Worker has no method '{method}'."));
        }
    }

    private DumpIndex RequireRuntime()
    {
        if (_index is null)
        {
            throw new RpcException(new RpcError(RpcError.NoDumpOpen, "No dump is open."));
        }

        if (!_index.HasRuntime)
        {
            throw new RpcException(new RpcError(RpcError.NoDumpOpen, "The dump has no .NET runtime that could be loaded (DAC not found)."));
        }

        return _index;
    }

    private static T Params<T>(JsonElement? element) =>
        ContractJson.FromElement<T>(element)
        ?? throw new RpcException(new RpcError(RpcError.InvalidParams, $"Expected {typeof(T).Name} params."));

    public void Dispose() => _index?.Dispose();
}
