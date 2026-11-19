using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DumpDebugger.Tests;

/// <summary>The MCP tools, called as the SDK would call them: against real fixture dumps, asserting on the text a model reads.</summary>
[Collection(WorkspaceCollection.Name)]
public class McpToolTests
{
    [Fact]
    public async Task An_agent_can_open_a_dump_and_follow_the_deadlock_from_verdict_to_thread()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        await using var registry = new DumpRegistry(() => null);
        var tools = new DumpTools(registry);

        Assert.Contains("No dump is open", await tools.ListDumps());

        var opened = await tools.OpenDump(dump);
        Assert.Contains("Opened as dump 'd1'", opened);
        Assert.Contains("x64", opened);

        var diagnosis = await tools.GetDiagnosis(symptom: "hang");
        Assert.Contains("VERDICT [critical, high confidence]", diagnosis);
        Assert.Contains("The app is hung: 2 requests are deadlocked", diagnosis);
        Assert.Contains("OrderLedger._gate (lock/0x", diagnosis);
        Assert.Contains("Next step:", diagnosis);
        Assert.DoesNotContain("{{", diagnosis);

        var graph = await tools.GetWaitGraph();
        Assert.Contains("1 DEADLOCK CYCLE(S)", graph);
        Assert.Contains("InventoryCache._sync", graph);
        Assert.Contains("[IN CYCLE]", graph);
        Assert.Contains("holds OrderLedger._gate (lock/0x", graph);
        Assert.DoesNotContain("{{", graph);

        var groups = await tools.ListThreadGroups(state: "blocked");
        Assert.Contains("[IN DEADLOCK]", groups);
        var threadId = int.Parse(Regex.Match(graph, @"owned by #(\d+)").Groups[1].Value);
        var thread = await tools.GetThread(threadId);
        Assert.Contains($"Thread #{threadId}", thread);
        Assert.Contains("IN DEADLOCK", thread);
        Assert.Contains("Waiting for:", thread);
        Assert.Contains("[app]", thread);
        Assert.Contains("<- blocked here", thread);

        // The same path again is the same dump, not a second worker.
        Assert.Contains("Opened as dump 'd1'", await tools.OpenDump(dump));
        Assert.Single(registry.List());

        Assert.Equal("Closed dump 'd1'.", await tools.CloseDump("d1"));
        Assert.Empty(registry.List());
    }

    [Fact]
    public async Task Memory_tools_lead_from_a_type_to_what_keeps_its_instances_alive()
    {
        var dump = await FixtureDumps.GetAsync("leak");
        await using var registry = new DumpRegistry(() => null);
        var tools = new DumpTools(registry);
        await tools.OpenDump(dump);

        Assert.Contains("Managed heap:", await tools.GetHeapSummary());
        var types = await tools.ListHeapTypes(filter: "Contoso", limit: 5);
        Assert.Contains("Contoso.Orders.CartSnapshot: 120,000 instances", types);
        Assert.Contains("[app type]", types);

        var instances = await tools.ListInstances("Contoso.Orders.CartSnapshot", limit: 3);
        Assert.Contains("Showing 1-3 of 120,000 instances", instances);
        Assert.Contains("Pass offset=3 for the next page", instances);
        var address = Regex.Match(instances, @"object/(0x[0-9a-f]+)").Groups[1].Value;

        var view = await tools.InspectObject(address);
        Assert.Contains("Contoso.Orders.CartSnapshot", view);
        Assert.Contains("FIELDS", view);

        var roots = await tools.WhyAlive("object/" + address);
        Assert.Contains("PATH 1", roots);
        Assert.Contains("SessionStore.s_snapshots", roots);
        Assert.Contains("(the object asked about)", roots);

        Assert.Contains("via ", await tools.GetReferrers(address));
        Assert.Contains("large object", await tools.ListLargeObjects());
        Assert.Contains("pending async operation", await tools.GetAsyncStacks());
    }

    [Fact]
    public async Task Compare_names_the_leaking_type_and_its_root()
    {
        var baseline = await FixtureDumps.GetAsync("leak-baseline");
        var current = await FixtureDumps.GetAsync("leak");
        await using var registry = new DumpRegistry(() => null);
        var tools = new DumpTools(registry);
        var handle = (await registry.OpenAsync(current, default)).Id;

        var result = await tools.CompareDumps(baseline, handle);
        Assert.Contains("LEAK SUSPECTS", result);
        Assert.Contains("Contoso.Orders.CartSnapshot: 5,000 -> 120,000 instances", result);
        Assert.Contains("held by static SessionStore.s_snapshots", result);

        var same = await Assert.ThrowsAsync<McpException>(() => tools.CompareDumps(current, handle));
        Assert.Contains("same dump", same.Message);
    }

    [Fact]
    public async Task The_dump_open_in_the_app_is_shared_not_reopened_and_cannot_be_closed()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        await using var bridge = new BridgeHarness();
        await bridge.CallAsync<DumpOpened>("dump.open", new { path = dump });
        await using var registry = new DumpRegistry(() => bridge.Server.CurrentDump);
        var tools = new DumpTools(registry);

        Assert.Contains("app: ", await tools.ListDumps());
        Assert.Contains("Opened as dump 'app'", await tools.OpenDump(dump));
        Assert.Same(bridge.Server.CurrentDump, Assert.Single(registry.List()).Session);

        // No `dump` argument means the app's dump.
        Assert.Contains("managed threads", await tools.ListThreadGroups());

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.CloseDump("app"));
        Assert.Contains("can't be closed from here", ex.Message);
        Assert.NotNull(bridge.Server.CurrentDump);

        // When the user closes it, tools say so instead of failing obscurely.
        await bridge.CallAsync("dump.close");
        var gone = await Assert.ThrowsAsync<McpException>(() => tools.GetDiagnosis());
        Assert.Contains("No dump is open", gone.Message);
    }

    [Fact]
    public async Task Bad_arguments_come_back_as_messages_a_model_can_act_on()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        await using var registry = new DumpRegistry(() => null);
        var tools = new DumpTools(registry);

        var missing = await Assert.ThrowsAsync<McpException>(() => tools.OpenDump(@"C:\nope\missing.dmp"));
        Assert.Contains("File not found", missing.Message);

        await tools.OpenDump(dump);
        var unknown = await Assert.ThrowsAsync<McpException>(() => tools.GetDiagnosis(dump: "d9"));
        Assert.Contains("Open dumps: d1 (deadlock.dmp)", unknown.Message);

        var address = await Assert.ThrowsAsync<McpException>(() => tools.InspectObject("not-an-address"));
        Assert.Contains("isn't an object address", address.Message);

        var choice = await Assert.ThrowsAsync<McpException>(() => tools.ListThreadGroups(groupBy: "colour"));
        Assert.Contains("stack, request, flat", choice.Message);

        var thread = await Assert.ThrowsAsync<McpException>(() => tools.GetThread(99_999));
        Assert.Contains("No thread #99999", thread.Message);
    }

    [Fact]
    public async Task At_most_three_dumps_can_be_opened_by_agents()
    {
        await using var registry = new DumpRegistry(() => null);
        foreach (var scenario in new[] { "deadlock", "threadpool", "syncoverasync" })
        {
            await registry.OpenAsync(await FixtureDumps.GetAsync(scenario), default);
        }

        var ex = await Assert.ThrowsAsync<McpException>(async () => await registry.OpenAsync(await FixtureDumps.GetAsync("finalizer"), default));
        Assert.Contains("Close one with close_dump", ex.Message);
        Assert.Equal(["d1", "d2", "d3"], registry.List().Select(h => h.Id));
    }
}

/// <summary>The HTTP endpoint the app hosts: off by default, loopback, token-gated, closed to browsers.</summary>
[Collection(WorkspaceCollection.Name)]
public class McpHostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ddmcp-").FullName;

    private McpSettingsStore Store => new(Path.Combine(_dir, "mcp.json"));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task It_is_off_until_turned_on_and_remembers_the_choice()
    {
        var port = FreePort();
        await using (var host = new McpHost(() => null, Store))
        {
            var initial = await host.StartIfEnabledAsync();
            Assert.True(initial.Available);
            Assert.False(initial.Enabled);
            Assert.False(initial.Running);
            Assert.False(initial.AllowDacDownload);
            Assert.True(initial.Token.Length >= 40);

            var on = await host.ConfigureAsync(new McpSettingsParams(Enabled: true, Port: port), default);
            Assert.True(on.Running);
            Assert.Null(on.Error);
            Assert.Equal($"http://127.0.0.1:{port}/mcp", on.Url);
        }

        // The token is not readable from the settings file, and the next launch starts the server again with the same one.
        var token = Store.Load().Token;
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(_dir, "mcp.json")));
        await using (var relaunched = new McpHost(() => null, Store))
        {
            var status = await relaunched.StartIfEnabledAsync();
            Assert.True(status.Running);
            Assert.Equal(token, status.Token);

            var off = await relaunched.ConfigureAsync(new McpSettingsParams(Enabled: false), default);
            Assert.False(off.Running);
            using var http = new HttpClient();
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(off.Url));
        }
    }

    [Fact]
    public async Task Requests_need_the_token_and_must_not_come_from_a_browser()
    {
        await using var host = new McpHost(() => null, Store);
        var status = await host.ConfigureAsync(new McpSettingsParams(Enabled: true, Port: FreePort()), default);
        using var http = new HttpClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(http, status.Url, token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(http, status.Url, token: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(http, status.Url, status.Token, origin: "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(http, status.Url, status.Token, hostHeader: "evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(http, status.Url, status.Token)).StatusCode);

        // Rotating the token locks out whoever held the old one, without a restart.
        var rotated = await host.RegenerateTokenAsync(default);
        Assert.NotEqual(status.Token, rotated.Token);
        Assert.True(rotated.Running);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(http, status.Url, status.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(http, status.Url, rotated.Token)).StatusCode);
    }

    [Fact]
    public async Task A_port_in_use_is_reported_instead_of_thrown()
    {
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var port = ((IPEndPoint)squatter.LocalEndpoint).Port;

        await using var host = new McpHost(() => null, Store);
        var status = await host.ConfigureAsync(new McpSettingsParams(Enabled: true, Port: port), default);
        Assert.False(status.Running);
        Assert.Contains("already in use", status.Error);

        var invalid = await host.ConfigureAsync(new McpSettingsParams(Port: 80), default);
        Assert.Contains("between 1024 and 65535", invalid.Error);
    }

    [Fact]
    public async Task A_real_mcp_client_lists_the_tools_and_analyzes_a_dump_end_to_end()
    {
        var dump = await FixtureDumps.GetAsync("deadlock");
        await using var host = new McpHost(() => null, Store);
        var status = await host.ConfigureAsync(new McpSettingsParams(Enabled: true, Port: FreePort()), default);

        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(status.Url),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {status.Token}" },
        }));

        Assert.Contains("untrusted data", client.ServerInstructions);
        var tools = await client.ListToolsAsync();
        Assert.Equal(
            [
                "close_dump", "compare_dumps", "get_async_stacks", "get_diagnosis", "get_heap_summary", "get_referrers",
                "get_thread", "get_wait_graph", "inspect_object", "list_dumps", "list_heap_types", "list_instances",
                "list_large_objects", "list_thread_groups", "open_dump", "why_alive",
            ],
            tools.Select(t => t.Name).Order());
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));

        // Nothing an agent can call writes pins, exports reports or generates samples.
        Assert.DoesNotContain(tools, t => t.Name.Contains("pin") || t.Name.Contains("report") || t.Name.Contains("sample"));

        var opened = await client.CallToolAsync("open_dump", new Dictionary<string, object?> { ["path"] = dump });
        Assert.NotEqual(true, opened.IsError);
        Assert.Contains("Opened as dump 'd1'", Text(opened));

        var diagnosis = await client.CallToolAsync("get_diagnosis", new Dictionary<string, object?> { ["dump"] = "d1" });
        Assert.Contains("The app is hung: 2 requests are deadlocked", Text(diagnosis));

        // A tool failure reaches the model as an error result carrying the explanation.
        var failed = await client.CallToolAsync("get_thread", new Dictionary<string, object?> { ["id"] = 99_999 });
        Assert.True(failed.IsError);
        Assert.Contains("No thread #99999", Text(failed));

        // Turning the server off closes what agents opened.
        await host.ConfigureAsync(new McpSettingsParams(Enabled: false), default);
        Assert.Empty(host.Registry.List());
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, string url, string? token, string? origin = null, string? hostHeader = null)
    {
        const string Initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (hostHeader is not null)
        {
            request.Headers.Host = hostHeader;
        }

        return http.SendAsync(request);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
