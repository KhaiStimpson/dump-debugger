using System.Collections.Concurrent;
using System.Text.Json;
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

public class HostBridgeTests
{
    [Fact]
    public async Task Opens_the_deadlock_fixture_and_lists_its_threads_through_the_bridge()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        await using var bridge = new BridgeHarness();

        var opened = await bridge.CallAsync<Core.Contracts.DumpOpened>("dump.open", new { path = dump });
        Assert.Equal(Core.Dump.DumpArchitecture.X64, opened.Metadata.Architecture);

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
