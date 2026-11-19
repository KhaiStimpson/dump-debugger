using System.Collections.Concurrent;
using System.Text.Json;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

/// <summary>Native capabilities the dispatcher needs from whatever window hosts it.</summary>
public interface IHostPlatform
{
    Task<string?> PickDumpFileAsync();

    Task<string?> PickSaveFileAsync(string suggestedName, string extension);

    Task<bool> RequestDacDownloadAsync(Core.Dump.DumpMetadata metadata, CancellationToken ct);

    void SetTitlebarPassthrough(IReadOnlyList<Rect> rects);

    void SetTheme(AppTheme theme);
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
    private DumpSession? _dump;

    public HostRpcServer(IHostPlatform platform, Func<string, Task> sendToWeb, HostRpcServerOptions? options = null)
    {
        _platform = platform;
        _sendToWeb = sendToWeb;
        _options = options ?? new HostRpcServerOptions();
    }

    public WorkerSession? Session => _dump?.Worker;

    /// <summary>The dump open in the app, if any. The MCP server reads it so an agent can work on what's on screen.</summary>
    public DumpSession? CurrentDump => _dump;

    /// <summary>Set by the host when it can run the MCP server; null in tests and headless use.</summary>
    public IMcpControl? Mcp { get; set; }

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

    private Task DebugAsync(string stage, string detail, bool error = false) =>
        NotifyWebAsync("debug.event", new DebugNotification("host", stage, detail, error));

    private Task SendAsync(RpcMessage message) =>
        _sendToWeb(JsonSerializer.Serialize(message, ContractJson.Options));

    private async Task<JsonElement?> DispatchAsync(string method, JsonElement? @params, IReadOnlyList<string> attachedFiles, CancellationToken ct)
    {
        var info = RpcCatalog.Find(method)
            ?? throw new RpcException(new RpcError(RpcError.MethodNotFound, $"Unknown method '{method}'."));

        if (info.Target == RpcTarget.Worker)
        {
            var dump = _dump ?? throw new RpcException(new RpcError(RpcError.NoDumpOpen, "No dump is open."));
            return await dump.Connection.CallRawAsync(method, @params, ct).ConfigureAwait(false);
        }

        return await DispatchHostAsync(method, @params, attachedFiles, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement?> DispatchHostAsync(string method, JsonElement? @params, IReadOnlyList<string> attachedFiles, CancellationToken ct)
    {
        switch (method)
        {
            case "app.info":
                var sessionAssembly = typeof(HostRpcServer).Assembly.Location;
                await DebugAsync("build", $"Session DLL: {sessionAssembly}; modified {File.GetLastWriteTimeUtc(sessionAssembly):O}").ConfigureAwait(false);
                return ContractJson.ToElement(new AppInfo(
                    typeof(HostRpcServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    AppPaths.DataRoot,
                    _options.StartupDumpPath));

            case "window.setTitlebar":
                _platform.SetTitlebarPassthrough(Params<TitlebarRegions>(@params).Passthrough);
                return ContractJson.ToElement(new Ack());

            case "window.setTheme":
                _platform.SetTheme(Params<ThemeParams>(@params).Theme);
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

            case "analysis.get":
                return ContractJson.ToElement(new AnalysisState(_dump?.Latest, _dump?.AnalysisRunning ?? false));

            case "recents.list":
                return ContractJson.ToElement(_recents.List());

            case "recents.remove":
                _recents.Remove(Params<PathParams>(@params).Path);
                return ContractJson.ToElement(new Ack());

            case "samples.list":
                return ContractJson.ToElement(SampleGenerator.Scenarios);

            case "samples.generate":
                return ContractJson.ToElement(await GenerateSampleAsync(Params<GenerateSampleParams>(@params).Scenario, ct).ConfigureAwait(false));

            case "pins.list":
                return ContractJson.ToElement(ListPins());

            case "pins.upsert":
                return ContractJson.ToElement(UpsertPin(Params<PinUpsert>(@params)));

            case "pins.remove":
                return ContractJson.ToElement(RemovePin(Params<UriParams>(@params).Uri));

            case "report.save":
                {
                    var p = Params<SaveReportParams>(@params);
                    var extension = p.Format == ReportFormat.Html ? ".html" : ".md";
                    var path = await _platform.PickSaveFileAsync(Path.ChangeExtension(p.FileName, extension), extension).ConfigureAwait(false);
                    if (path is not null)
                    {
                        await File.WriteAllTextAsync(path, p.Content, ct).ConfigureAwait(false);
                    }

                    return ContractJson.ToElement(new SavedReport(path));
                }

            case "compare.candidates":
                return ContractJson.ToElement(CompareCandidates());

            case "compare.run":
                return ContractJson.ToElement(await CompareAsync(Params<CompareParams>(@params), ct).ConfigureAwait(false));

            case "analysis.cancel":
                _dump?.CancelAnalysis();
                return ContractJson.ToElement(new Ack());

            case "mcp.get":
                return ContractJson.ToElement(Mcp?.Status ?? McpUnavailable);

            case "mcp.set":
                return ContractJson.ToElement(Mcp is { } mcp ? await mcp.ConfigureAsync(Params<McpSettingsParams>(@params), ct).ConfigureAwait(false) : McpUnavailable);

            case "mcp.regenerateToken":
                return ContractJson.ToElement(Mcp is { } control ? await control.RegenerateTokenAsync(ct).ConfigureAwait(false) : McpUnavailable);

            default:
                throw new RpcException(new RpcError(RpcError.MethodNotFound, $"Host method '{method}' is not implemented."));
        }
    }

    private static readonly McpStatus McpUnavailable =
        new(false, false, false, 0, "", "", false, "The MCP server isn't available in this build.");

    /// <summary>
    /// Opens the dump in a new <see cref="DumpSession"/> (closing the previous one). Returns as
    /// soon as the dump is open, so the UI can show the Analyzing screen while stages complete.
    /// </summary>
    public async Task<DumpOpened> OpenDumpAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new RpcException(new RpcError(RpcError.InvalidParams, $"File not found: {path}"));
        }

        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CloseDumpCoreAsync().ConfigureAwait(false);

            var dump = await DumpSession.OpenAsync(path, new DumpSessionHooks(
                Notify: (method, payload) => SendAsync(RpcMessage.Notification(method, payload)),
                RequestDacDownload: _platform.RequestDacDownloadAsync,
                AnalysisCompleted: (workspace, diagnosis) => _recents.UpdateVerdict(workspace.DumpPath, diagnosis)), ct).ConfigureAwait(false);
            _dump = dump;
            _recents.Touch(dump.Metadata, dump.Latest);
            return new DumpOpened(dump.Metadata, dump.Workspace.Directory, dump.CachedAnalysis, dump.AnalysisError);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public Task? AnalysisTask => _dump?.AnalysisTask;

    public Diagnosis? LatestDiagnosis => _dump?.Latest;

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
        if (_dump is { } old)
        {
            _dump = null;
            await old.DisposeAsync().ConfigureAwait(false);
        }
    }

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
