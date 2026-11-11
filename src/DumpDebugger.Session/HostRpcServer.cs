using System.Collections.Concurrent;
using System.Text.Json;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

/// <summary>Native capabilities the dispatcher needs from whatever window hosts it.</summary>
public interface IHostPlatform
{
    Task<string?> PickDumpFileAsync();

    Task<string?> PickSaveFileAsync(string suggestedName, string extension);

    void SetTitlebarPassthrough(IReadOnlyList<Rect> rects);
}

public sealed record HostRpcServerOptions(string? StartupDumpPath = null);

/// <summary>
/// The host end of the WebView2 bridge. Each message from the web app is routed by
/// <see cref="RpcCatalog"/>: host methods run here, worker methods are forwarded to the open
/// <see cref="WorkerSession"/> with their params untouched, and worker notifications are
/// relayed back. Nothing here touches UI types, so tests can drive it with plain JSON.
/// </summary>
public sealed partial class HostRpcServer : IAsyncDisposable
{
    private readonly IHostPlatform _platform;
    private readonly Func<string, Task> _sendToWeb;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly HostRpcServerOptions _options;
    private WorkerSession? _session;

    public HostRpcServer(IHostPlatform platform, Func<string, Task> sendToWeb, HostRpcServerOptions? options = null)
    {
        _platform = platform;
        _sendToWeb = sendToWeb;
        _options = options ?? new HostRpcServerOptions();
    }

    public WorkerSession? Session => _session;

    /// <summary>Handles one raw message from the web app. Never throws: failures become RPC errors.</summary>
    public async Task HandleWebMessageAsync(string json, IReadOnlyList<string>? attachedFiles = null)
    {
        RpcMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<RpcMessage>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            return;
        }

        if (message is null)
        {
            return;
        }

        if (message is { IsNotification: true, Method: RpcConnection.CancelMethod })
        {
            var cancel = ContractJson.FromElement<CancelParams>(message.Params);
            if (cancel is not null && _inflight.TryGetValue(cancel.Id, out var cts))
            {
                cts.Cancel();
            }

            return;
        }

        if (!message.IsRequest)
        {
            return;
        }

        var id = message.Id!;
        using var requestCts = new CancellationTokenSource();
        _inflight[id] = requestCts;
        RpcMessage response;
        try
        {
            var result = await DispatchAsync(message.Method!, message.Params, attachedFiles ?? [], requestCts.Token).ConfigureAwait(false);
            response = RpcMessage.Success(id, result ?? RpcConnection.NullElement);
        }
        catch (RpcException ex)
        {
            response = RpcMessage.Failure(id, ex.Error);
        }
        catch (OperationCanceledException)
        {
            response = RpcMessage.Failure(id, new RpcError(RpcError.Cancelled, "Request cancelled."));
        }
        catch (Exception ex)
        {
            response = RpcMessage.Failure(id, new RpcError(RpcError.InternalError, ex.Message, ex.ToString()));
        }
        finally
        {
            _inflight.TryRemove(id, out _);
        }

        await SendAsync(response).ConfigureAwait(false);
    }

    public Task NotifyWebAsync<T>(string method, T payload) =>
        SendAsync(RpcMessage.Notification(method, ContractJson.ToElement(payload)));

    private Task SendAsync(RpcMessage message) =>
        _sendToWeb(JsonSerializer.Serialize(message, ContractJson.Options));

    private async Task<JsonElement?> DispatchAsync(string method, JsonElement? @params, IReadOnlyList<string> attachedFiles, CancellationToken ct)
    {
        var info = RpcCatalog.Find(method)
            ?? throw new RpcException(new RpcError(RpcError.MethodNotFound, $"Unknown method '{method}'."));

        if (info.Target == RpcTarget.Worker)
        {
            var session = _session ?? throw new RpcException(new RpcError(RpcError.NoDumpOpen, "No dump is open."));
            return await session.Connection.CallRawAsync(method, @params, ct).ConfigureAwait(false);
        }

        return await DispatchHostAsync(method, @params, attachedFiles, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement?> DispatchHostAsync(string method, JsonElement? @params, IReadOnlyList<string> attachedFiles, CancellationToken ct)
    {
        switch (method)
        {
            case "app.info":
                return ContractJson.ToElement(new AppInfo(
                    typeof(HostRpcServer).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    AppPaths.DataRoot,
                    _options.StartupDumpPath));

            case "window.setTitlebar":
                _platform.SetTitlebarPassthrough(Params<TitlebarRegions>(@params).Passthrough);
                return ContractJson.ToElement(new Ack());

            case "dump.pick":
                return ContractJson.ToElement(new PickedFile(await _platform.PickDumpFileAsync().ConfigureAwait(false)));

            case "dump.open":
                {
                    var p = Params<OpenDumpParams>(@params);
                    var path = string.IsNullOrEmpty(p.Path) && attachedFiles.Count > 0 ? attachedFiles[0] : p.Path;
                    return ContractJson.ToElement(await OpenDumpAsync(path, ct).ConfigureAwait(false));
                }

            case "dump.close":
                await CloseDumpAsync().ConfigureAwait(false);
                return ContractJson.ToElement(new Ack());

            default:
                throw new RpcException(new RpcError(RpcError.MethodNotFound, $"Host method '{method}' is not implemented."));
        }
    }

    public async Task<Core.Dump.DumpMetadata> OpenDumpAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new RpcException(new RpcError(RpcError.InvalidParams, $"File not found: {path}"));
        }

        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CloseDumpCoreAsync().ConfigureAwait(false);

            await NotifyWebAsync("dump.progress", new ProgressNotification("Starting worker", 0.02, path)).ConfigureAwait(false);
            var session = await WorkerSession.StartAsync(path, RelayWorkerNotification, ct).ConfigureAwait(false);
            _session = session;
            await NotifyWebAsync("worker.status", new WorkerStatus(WorkerState.Starting, session.Architecture.ToString(), session.ProcessId, null)).ConfigureAwait(false);

            var metadata = await session.OpenDumpAsync(path, ct).ConfigureAwait(false);
            await NotifyWebAsync("worker.status", new WorkerStatus(WorkerState.Ready, session.Architecture.ToString(), session.ProcessId, null)).ConfigureAwait(false);
            return metadata;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task CloseDumpAsync()
    {
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseDumpCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task CloseDumpCoreAsync()
    {
        if (_session is { } old)
        {
            _session = null;
            await old.DisposeAsync().ConfigureAwait(false);
            await NotifyWebAsync("worker.status", new WorkerStatus(WorkerState.Exited, old.Architecture.ToString(), null, null)).ConfigureAwait(false);
        }
    }

    private void RelayWorkerNotification(string method, JsonElement? payload) =>
        _ = SendAsync(RpcMessage.Notification(method, payload));

    private static T Params<T>(JsonElement? element) =>
        ContractJson.FromElement<T>(element)
        ?? throw new RpcException(new RpcError(RpcError.InvalidParams, $"Expected {typeof(T).Name} params."));

    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _inflight.Values)
        {
            cts.Cancel();
        }

        await CloseDumpAsync().ConfigureAwait(false);
    }
}
