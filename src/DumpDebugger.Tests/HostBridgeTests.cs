using System.Collections.Concurrent;
using System.Text.Json;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Rpc;
using DumpDebugger.Session;

namespace DumpDebugger.Tests;

/// <summary>Drives <see cref="HostRpcServer"/> exactly as the WebView2 page does: JSON in, JSON out.</summary>
internal sealed class BridgeHarness : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcMessage>> _responses = new();
    private int _next;

    public BridgeHarness()
    {
        Server = new HostRpcServer(new HeadlessPlatform(), json =>
        {
            var message = JsonSerializer.Deserialize<RpcMessage>(json, ContractJson.Options)!;
            if (message.IsResponse)
            {
                _responses.GetOrAdd(message.Id!, _ => new()).TrySetResult(message);
            }
            else if (message.IsNotification)
            {
                Notifications.Enqueue(message);
            }

            return Task.CompletedTask;
        });
    }

    public HostRpcServer Server { get; }

    public ConcurrentQueue<RpcMessage> Notifications { get; } = new();

    public async Task<JsonElement> CallAsync(string method, object? @params = null, TimeSpan? timeout = null)
    {
        var id = $"t{Interlocked.Increment(ref _next)}";
        var tcs = _responses.GetOrAdd(id, _ => new());
        var json = JsonSerializer.Serialize(new { id, method, @params = @params ?? new { } }, ContractJson.Options);
        await Server.HandleWebMessageAsync(json);
        var response = await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(120));
        if (response.Error is not null)
        {
            throw new RpcException(response.Error);
        }

        return response.Result ?? default;
    }

    public async Task<T> CallAsync<T>(string method, object? @params = null, TimeSpan? timeout = null) =>
        ContractJson.FromElement<T>(await CallAsync(method, @params, timeout))!;

    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

[Collection(WorkspaceCollection.Name)]
public class HostBridgeTests
{
    [Fact]
    public async Task Unexpected_worker_exit_is_reported_with_an_exit_code()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        var exited = new TaskCompletionSource<Core.Contracts.WorkerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await WorkerSession.StartAsync(dump, (method, payload) =>
        {
            if (method == "worker.status" && ContractJson.FromElement<Core.Contracts.WorkerStatus>(payload) is { } status)
                exited.TrySetResult(status);
        });
        using var process = System.Diagnostics.Process.GetProcessById(session.ProcessId);
        process.Kill();
        var notification = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Core.Contracts.WorkerState.Exited, notification.State);
        Assert.Contains("exit code", notification.Message);
    }

    [Fact]
    public async Task The_consent_reload_replaces_the_open_index_in_the_same_worker()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        var events = new ConcurrentQueue<Core.Contracts.DebugNotification>();
        await using var session = await WorkerSession.StartAsync(dump, (method, payload) =>
        {
            if (method == "debug.event" && ContractJson.FromElement<Core.Contracts.DebugNotification>(payload) is { } e)
                events.Enqueue(e);
        });

        var offline = await session.OpenDumpAsync(dump);
        // The local runtime-folder DAC is ClrMD's first candidate, so allowing downloads makes no request here.
        var online = await session.OpenDumpAsync(dump, default, allowSymbolServer: true);

        Assert.Equal(offline.RuntimeIdentity(), online.RuntimeIdentity());
        Assert.Equal(Core.Dump.DacResolutionTier.LocalMachine, online.PrimaryRuntime()!.DacTier);
        Assert.Contains(events, e => e.Stage == "Opening dump" && e.Detail.Contains("msdl.microsoft.com allowed"));
        Assert.DoesNotContain(events, e => e.Stage == "Downloading DAC");
        var groups = await session.Connection.CallAsync<Core.Contracts.ThreadGroupsResult>("threads.groups", new Core.Contracts.ThreadGroupsQuery(Core.Contracts.ThreadGrouping.Flat, Core.Contracts.ThreadStateFilter.All, null));
        Assert.True(groups!.TotalThreads >= 3);
    }

    [Fact]
    public async Task Opens_the_deadlock_fixture_and_lists_its_threads_through_the_bridge()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        await using var bridge = new BridgeHarness();

        var opened = await bridge.CallAsync<Core.Contracts.DumpOpened>("dump.open", new { path = dump });
        Assert.Equal(Core.Dump.DumpArchitecture.X64, opened.Metadata.Architecture);
        Assert.Contains(bridge.Notifications, n => n.Method == "debug.event"
            && ContractJson.FromElement<Core.Contracts.DebugNotification>(n.Params) is { Source: "worker", Stage: "build" });
        Assert.Contains(bridge.Notifications, n => n.Method == "debug.event"
            && ContractJson.FromElement<Core.Contracts.DebugNotification>(n.Params) is { Source: "host", Stage: "runtime lookup" });

        var groups = await bridge.CallAsync<Core.Contracts.ThreadGroupsResult>("threads.groups", new { groupBy = "flat", state = "all" });
        Assert.True(groups.TotalThreads >= 3);
    }

    [Fact]
    public async Task Worker_methods_fail_cleanly_before_a_dump_is_open()
    {
        await using var bridge = new BridgeHarness();
        var ex = await Assert.ThrowsAsync<RpcException>(() => bridge.CallAsync("threads.groups", new { groupBy = "stack", state = "all" }));
        Assert.Equal(RpcError.NoDumpOpen, ex.Error.Code);
    }

    [Fact]
    public async Task Unknown_methods_are_rejected()
    {
        await using var bridge = new BridgeHarness();
        var ex = await Assert.ThrowsAsync<RpcException>(() => bridge.CallAsync("nope.nothing"));
        Assert.Equal(RpcError.MethodNotFound, ex.Error.Code);
    }
}
