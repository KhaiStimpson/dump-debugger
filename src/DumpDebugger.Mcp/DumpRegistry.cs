using DumpDebugger.Core.Rpc;
using DumpDebugger.Session;
using ModelContextProtocol;

namespace DumpDebugger.Mcp;

/// <summary>A dump an agent can address: the one open in the app (<c>app</c>) or one it opened itself (<c>d1</c>, <c>d2</c>…).</summary>
public sealed record DumpHandle(string Id, DumpSession Session, bool IsApp);

/// <summary>
/// The dumps the MCP tools can see. The app's open dump is shared (same worker, no second copy
/// of the heap); dumps an agent opens by path get their own workers, capped so a runaway agent
/// can't fill the machine with multi-GB heaps. Nothing here is visible in the app's own UI.
/// </summary>
public sealed class DumpRegistry : IAsyncDisposable
{
    public const string AppId = "app";
    public const int MaxAgentDumps = 3;

    private readonly Func<DumpSession?> _appDump;
    private readonly Func<bool> _allowDacDownload;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<DumpHandle> _agent = [];
    private int _next;

    public DumpRegistry(Func<DumpSession?> appDump, Func<bool>? allowDacDownload = null)
    {
        _appDump = appDump;
        _allowDacDownload = allowDacDownload ?? (() => false);
    }

    public IReadOnlyList<DumpHandle> List()
    {
        var list = new List<DumpHandle>();
        if (_appDump() is { } app)
        {
            list.Add(new DumpHandle(AppId, app, true));
        }

        List<DumpHandle>? dead = null;
        lock (_agent)
        {
            // A worker that crashed (out of memory on a huge heap, a ClrMD fault) leaves a session
            // nothing can query: drop it here so the id stops resolving and the path can be reopened.
            for (var i = _agent.Count - 1; i >= 0; i--)
            {
                if (_agent[i].Session.Worker.HasExited)
                {
                    (dead ??= []).Add(_agent[i]);
                    _agent.RemoveAt(i);
                }
            }

            list.AddRange(_agent);
        }

        if (dead is not null)
        {
            _ = DisposeDeadAsync(dead);
        }

        return list;
    }

    private static async Task DisposeDeadAsync(List<DumpHandle> dead)
    {
        foreach (var handle in dead)
        {
            try
            {
                await handle.Session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Already gone: there is nothing left to release.
            }
        }
    }

    /// <summary>Resolves a handle id or the path of an open dump. With no argument: the app's dump, else the only open one.</summary>
    public DumpHandle Resolve(string? dump)
    {
        var open = List();
        if (string.IsNullOrWhiteSpace(dump))
        {
            return open.FirstOrDefault(h => h.IsApp)
                ?? (open.Count == 1 ? open[0] : null)
                ?? throw new McpException(open.Count == 0
                    ? "No dump is open. Open one in the Dump Debugger app, or call open_dump with the path to a .dmp file."
                    : $"Several dumps are open and none is the app's; pass `dump` as one of: {string.Join(", ", open.Select(h => h.Id))}.");
        }

        var id = dump.Trim();
        return open.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? open.FirstOrDefault(h => IsPath(id) && h.Session.IsFor(id))
            ?? throw new McpException(open.Count == 0
                ? $"No dump '{id}' is open (nothing is). Call open_dump with the path to a .dmp file."
                : $"No dump '{id}' is open. Open dumps: {string.Join(", ", open.Select(h => $"{h.Id} ({Path.GetFileName(h.Session.DumpPath)})"))}.");
    }

    public async Task<DumpHandle> OpenAsync(string path, CancellationToken ct)
    {
        if (!IsPath(path) || !File.Exists(path))
        {
            throw new McpException($"File not found: {path}. Pass the full path to a .dmp file on this machine.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (List().FirstOrDefault(h => h.Session.IsFor(path)) is { } existing)
            {
                return existing;
            }

            int count;
            lock (_agent)
            {
                count = _agent.Count;
            }

            if (count >= MaxAgentDumps)
            {
                throw new McpException($"{MaxAgentDumps} dumps are already open ({string.Join(", ", List().Where(h => !h.IsApp).Select(h => h.Id))}). Close one with close_dump first.");
            }

            DumpSession session;
            try
            {
                session = await DumpSession.OpenAsync(path, new DumpSessionHooks(
                    RequestDacDownload: (_, _) => Task.FromResult(_allowDacDownload())), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RpcException or IOException or InvalidOperationException or NotSupportedException or InvalidDataException)
            {
                throw new McpException($"Could not open {path}: {ex.Message}");
            }

            var handle = new DumpHandle($"d{++_next}", session, false);
            lock (_agent)
            {
                _agent.Add(handle);
            }

            return handle;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync(string dump)
    {
        var handle = Resolve(dump);
        if (handle.IsApp)
        {
            throw new McpException("That dump is open in the Dump Debugger app and stays under the user's control; it can't be closed from here.");
        }

        lock (_agent)
        {
            _agent.Remove(handle);
        }

        await handle.Session.DisposeAsync().ConfigureAwait(false);
    }

    public async Task CloseAllAsync()
    {
        DumpHandle[] all;
        lock (_agent)
        {
            all = [.. _agent];
            _agent.Clear();
        }

        foreach (var handle in all)
        {
            await handle.Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsPath(string value)
    {
        try
        {
            return Path.IsPathFullyQualified(value);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => new(CloseAllAsync());
}
