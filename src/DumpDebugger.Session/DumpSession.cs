using System.Text.Json;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

/// <summary>What an owner of a <see cref="DumpSession"/> wants to hear about or decide. All optional.</summary>
/// <param name="Notify">Receives every host and worker notification for this dump (progress, debug events, streamed diagnoses).</param>
/// <param name="RequestDacDownload">Asked once when a runtime's DAC isn't available offline; false stays offline.</param>
/// <param name="AnalysisCompleted">Called after a fresh analysis has been cached in the workspace.</param>
public sealed record DumpSessionHooks(
    Func<string, JsonElement?, Task>? Notify = null,
    Func<DumpMetadata, CancellationToken, Task<bool>>? RequestDacDownload = null,
    Action<Workspace, Diagnosis>? AnalysisCompleted = null);

/// <summary>
/// One open dump: its architecture-matched worker, its workspace sidecar, and the analysis that
/// streams in the background. Opening restores the cached diagnosis when there is one, otherwise
/// starts analysis; either way the compare snapshot is captured afterwards. The app holds one of
/// these for the dump on screen; the MCP server holds one per dump an agent opened.
/// </summary>
public sealed class DumpSession : IAsyncDisposable
{
    private readonly DumpSessionHooks _hooks;
    private CancellationTokenSource? _analysisCts;
    private volatile bool _disposed;

    private DumpSession(WorkerSession worker, DumpSessionHooks hooks)
    {
        Worker = worker;
        _hooks = hooks;
    }

    public WorkerSession Worker { get; }

    public RpcConnection Connection => Worker.Connection;

    public Workspace Workspace { get; private set; } = null!;

    public DumpMetadata Metadata { get; private set; } = null!;

    public string DumpPath => Workspace.DumpPath;

    public string RuntimeIdentity { get; private set; } = "";

    /// <summary>The newest diagnosis: cached, streamed mid-analysis, or final.</summary>
    public Diagnosis? Latest { get; private set; }

    public bool CachedAnalysis { get; private set; }

    /// <summary>Why analysis can't start at all (no loadable runtime), or null.</summary>
    public string? AnalysisError { get; private set; }

    /// <summary>The message of an analysis run that started and then failed, or null.</summary>
    public string? AnalysisFailure { get; private set; }

    public Task? AnalysisTask { get; private set; }

    public Task? SnapshotTask { get; private set; }

    public bool AnalysisRunning => AnalysisTask is { IsCompleted: false };

    /// <summary>
    /// Starts the matching worker and opens the dump (offline first, at most one consented online
    /// retry). Returns as soon as the dump is open, so callers can show stages while analysis runs.
    /// </summary>
    public static async Task<DumpSession> OpenAsync(string path, DumpSessionHooks hooks, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new RpcException(new RpcError(RpcError.InvalidParams, $"File not found: {path}"));
        }

        await Send(hooks, "debug.event", new DebugNotification("host", "open", path)).ConfigureAwait(false);
        await Send(hooks, "dump.progress", new ProgressNotification("Starting worker", 0.02, path)).ConfigureAwait(false);

        DumpSession? session = null;
        var worker = await WorkerSession.StartAsync(path, (method, payload) => session?.RelayWorkerNotification(method, payload), ct).ConfigureAwait(false);
        session = new DumpSession(worker, hooks);
        try
        {
            await session.OpenCoreAsync(path, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task OpenCoreAsync(string path, CancellationToken ct)
    {
        var arch = Worker.Architecture.ToString();
        await DebugAsync("worker started", $"{Worker.Architecture}; pid {Worker.ProcessId}; {WorkerLauncher.FindWorkerExecutable(Worker.Architecture)}").ConfigureAwait(false);
        await NotifyAsync("worker.status", new WorkerStatus(WorkerState.Starting, arch, Worker.ProcessId, null)).ConfigureAwait(false);

        Metadata = await DacConsent.OpenAsync(
            async (online, token) =>
            {
                await DebugAsync("runtime lookup", online ? "Online retry (consent granted)" : "Offline lookup").ConfigureAwait(false);
                if (online)
                    await NotifyAsync("dump.progress", new ProgressNotification("Downloading matching DACs", 0.12, "Contacting the Microsoft symbol server…")).ConfigureAwait(false);
                return await Worker.OpenDumpAsync(path, token, online).ConfigureAwait(false);
            },
            async (metadata, token) =>
            {
                if (_hooks.RequestDacDownload is null)
                {
                    return false;
                }

                await DebugAsync("consent", "Waiting for a decision on downloading DACs").ConfigureAwait(false);
                var allowed = await _hooks.RequestDacDownload(metadata, token).ConfigureAwait(false);
                await DebugAsync("consent", allowed ? "Download approved" : "Staying offline").ConfigureAwait(false);
                return allowed;
            }, ct).ConfigureAwait(false);
        await NotifyAsync("worker.status", new WorkerStatus(WorkerState.Ready, arch, Worker.ProcessId, null)).ConfigureAwait(false);

        Workspace = Workspace.For(path);
        RuntimeIdentity = Metadata.RuntimeIdentity();
        var cached = Workspace.TryLoadDiagnosis(RuntimeIdentity);
        if (cached is not null)
        {
            await DebugAsync("analysis", $"Restoring cached diagnosis for {RuntimeIdentity}").ConfigureAwait(false);
            CachedAnalysis = true;
            Latest = cached with { FromCache = true };
            await NotifyAsync("analysis.updated", Latest).ConfigureAwait(false);
            EnsureSnapshotInBackground(cached);
        }
        else if (Metadata.Runtimes.Any(r => r.IsManagedAnalysisAvailable))
        {
            await DebugAsync("analysis", $"Starting worker analysis (no cached diagnosis for {RuntimeIdentity})").ConfigureAwait(false);
            StartAnalysis();
        }
        else
        {
            AnalysisError = DacConsent.AnalysisError(Metadata);
            await DebugAsync("analysis unavailable", AnalysisError ?? "No usable runtime", true).ConfigureAwait(false);
        }
    }

    private void StartAnalysis()
    {
        _analysisCts = new CancellationTokenSource();
        var ct = _analysisCts.Token;
        Latest = null;
        AnalysisTask = Task.Run(async () =>
        {
            try
            {
                var final = await Connection.CallAsync<Diagnosis>("analysis.run", new AnalysisRunParams(), ct).ConfigureAwait(false);
                if (final is not null)
                {
                    Latest = final;
                    Workspace.SaveDiagnosis(final, RuntimeIdentity);
                    _hooks.AnalysisCompleted?.Invoke(Workspace, final);
                    EnsureSnapshotInBackground(final);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await DebugAsync("analysis stopped", "Cancelled").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await DebugAsync("analysis failed", ex.Message, true).ConfigureAwait(false);
                if (!_disposed && !ct.IsCancellationRequested)
                {
                    AnalysisFailure = ex.Message;
                    await NotifyAsync("analysis.failed", new AnalysisFailure(ex.Message)).ConfigureAwait(false);
                }
            }
        });
    }

    public void CancelAnalysis() => _analysisCts?.Cancel();

    /// <summary>After analysis, capture the compare snapshot in the background (once per dump and engine version).</summary>
    private void EnsureSnapshotInBackground(Diagnosis diagnosis)
    {
        if (Workspace.HasSnapshotFor(RuntimeIdentity))
        {
            return;
        }

        SnapshotTask = Task.Run(async () =>
        {
            try
            {
                var snapshot = await Connection.CallAsync<HeapSnapshot>("snapshot.build", new SnapshotParams(diagnosis)).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    Workspace.SaveSnapshot(snapshot, RuntimeIdentity);
                    await NotifyAsync("snapshot.ready", new PathParams(Workspace.DumpPath)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is RpcException or IOException or OperationCanceledException)
            {
                // The dump was closed mid-way; the next open tries again.
            }
        });
    }

    /// <summary>This dump's compare snapshot: cached, being built after analysis, or built now.</summary>
    public async Task<HeapSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (Workspace.TryLoadSnapshot(RuntimeIdentity) is { } cached)
        {
            return cached;
        }

        if (AnalysisTask is { } analysis)
        {
            await analysis.WaitAsync(ct).ConfigureAwait(false);
        }

        if (SnapshotTask is { } snap)
        {
            await snap.WaitAsync(ct).ConfigureAwait(false);
        }

        if (Workspace.TryLoadSnapshot(RuntimeIdentity) is { } built)
        {
            return built;
        }

        var diagnosis = Latest ?? throw new RpcException(new RpcError(RpcError.InternalError, "The open dump hasn't been analyzed yet."));
        var fresh = await Connection.CallAsync<HeapSnapshot>("snapshot.build", new SnapshotParams(diagnosis), ct).ConfigureAwait(false);
        Workspace.SaveSnapshot(fresh!, RuntimeIdentity);
        return fresh!;
    }

    public bool IsFor(string? path) => SamePath(path, DumpPath);

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private void RelayWorkerNotification(string method, JsonElement? payload)
    {
        if (method == "analysis.updated" && ContractJson.FromElement<Diagnosis>(payload) is { } d)
        {
            Latest = d;
        }

        _ = _hooks.Notify?.Invoke(method, payload);
    }

    private Task NotifyAsync<T>(string method, T payload) => Send(_hooks, method, payload);

    private Task DebugAsync(string stage, string detail, bool error = false) =>
        NotifyAsync("debug.event", new DebugNotification("host", stage, detail, error));

    private static Task Send<T>(DumpSessionHooks hooks, string method, T payload) =>
        hooks.Notify?.Invoke(method, ContractJson.ToElement(payload)) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _analysisCts?.Cancel();
        await Worker.DisposeAsync().ConfigureAwait(false);
        await NotifyAsync("worker.status", new WorkerStatus(WorkerState.Exited, Worker.Architecture.ToString(), null, null)).ConfigureAwait(false);
    }
}
