using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Heap;
using DumpDebugger.Analysis.Threads;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// ADO.NET connection-pool exhaustion: every connection a pool may open is checked out, and
/// callers wait in <c>Open()</c> for one to come back. Read from the pool itself (SqlClient's
/// <c>DbConnectionPool</c>, or <c>WaitHandleDbConnectionPool</c> in Microsoft.Data.SqlClient 6):
/// its size and limit, the threads and async opens waiting, and for each checked-out connection
/// who holds it (a thread, a suspended async method, a field) or that nothing does (a leak).
/// </summary>
public sealed class ConnectionPoolAnalyzer : IFindingAnalyzer
{
    private const int MaxConnections = 2_000;

    public string Name => "connection-pool";

    public AnalyzerStage Stage => AnalyzerStage.Heap;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        if (context.Explorer is not { } explorer)
        {
            yield break;
        }

        var index = explorer.Index;
        var poolTypes = new HashSet<int>();
        for (var t = 0; t < index.Types.Count; t++)
        {
            var type = index.Types[t];
            if (type.Name?.EndsWith("DbConnectionPool", StringComparison.Ordinal) == true && type.GetFieldByName("_totalObjects") is not null)
            {
                poolTypes.Add(t);
            }
        }

        if (poolTypes.Count == 0)
        {
            yield break;
        }

        var heap = context.Runtime.Heap;
        var results = new List<AnalyzerResult>();
        for (var i = 0; i < index.Count; i++)
        {
            if (!poolTypes.Contains(index.TypeIds[i]))
            {
                continue;
            }

            var pool = heap.GetObject(index.Addresses[i], index.TypeOf(i));
            if (Analyze(context, explorer, pool) is { } result)
            {
                results.Add(result);
            }
        }

        foreach (var result in results.OrderByDescending(r => r.Finding.Score).Take(3))
        {
            yield return result;
        }
    }

    private AnalyzerResult? Analyze(AnalysisContext context, ObjectExplorer explorer, ClrObject pool)
    {
        if (!pool.TryReadField<int>("_totalObjects", out var total)
            || !pool.TryReadObjectField("_connectionPoolGroupOptions", out var options) || options.IsNull
            || !options.TryReadField<int>("_maxPoolSize", out var max) || max <= 0)
        {
            return null;
        }

        var blocked = pool.TryReadField<int>("_waitCount", out var w) ? Math.Max(0, w) : 0;
        var pendingAsync = pool.TryReadObjectField("_pendingOpens", out var pending) && !pending.IsNull
            ? ThreadPoolInspector.ConcurrentQueueCount(pending)
            : 0;
        var timeoutSeconds = options.TryReadField<int>("_creationTimeout", out var ms) && ms > 0 ? ms / 1000 : (int?)null;

        var connections = Connections(pool);
        var idle = connections.Count(c => c.TryReadField<int>("_pooledCount", out var pooled) && pooled > 0);
        var inUse = Math.Max(0, total - idle);
        var waiting = blocked + pendingAsync;
        var full = total >= max && idle == 0;
        if (!full || (waiting == 0 && inUse < 2))
        {
            return null;
        }

        var target = Target(connections, pool);
        var waiters = context.Threads.Where(IsWaitingForPool).ToList();
        var topWaiters = waiters.GroupBy(t => t.WhereName ?? t.Where ?? "?").OrderByDescending(g => g.Count()).FirstOrDefault();
        var holders = connections.Where(c => !(c.TryReadField<int>("_pooledCount", out var pooled) && pooled > 0))
            .Select(c => Holder(context, explorer, c))
            .GroupBy(h => (h.Kind, h.Label))
            .Select(g => (g.Key.Kind, g.Key.Label, Count: g.Count(), g.First().Evidence, g.First().Detail))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Label, StringComparer.Ordinal)
            .ToList();

        var leaked = holders.Where(h => h.Kind == HolderKind.Leaked).Sum(h => h.Count);
        var where = target ?? "the database";
        var waitingText = waiting == 0 ? "" : $" and {Plural(waiting, "caller")} {(waiting == 1 ? "is" : "are")} waiting for one";

        var why = new List<string> { $"`Max Pool Size` {max} · {total} open, {idle} idle" };
        if (blocked > 0)
        {
            why.Add($"{Plural(blocked, "thread")} blocked in `Open()`" + (topWaiters is null ? "" : $", mostly {Code(topWaiters.Key)}"));
        }

        if (pendingAsync > 0)
        {
            why.Add($"{Plural(pendingAsync, "async open")} queued");
        }

        why.AddRange(holders.Take(3).Select(h => h.Kind == HolderKind.Leaked
            ? $"{h.Count} leaked"
            : $"{h.Count} held by {Code(h.Label)}"));

        var meaning = "ADO.NET keeps a pool of open connections per connection string, up to `Max Pool Size` (100 unless the connection string says otherwise). "
                      + "Every one of them is checked out, so `Open()` can't hand out a connection until someone closes theirs, and callers queue. "
                      + (timeoutSeconds is { } s
                          ? $"After the connect timeout ({s} s) each waiter fails with \"Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool\". "
                          : "When the connect timeout runs out each waiter fails with \"Timeout expired… prior to obtaining a connection from the pool\". ")
                      + "The database itself may be idle: the limit is in this process, and the cause is code that keeps connections open too long or never closes them.";

        var steps = new List<string>();
        foreach (var h in holders.Take(3))
        {
            steps.Add(h.Kind switch
            {
                HolderKind.Async => $"{Code(h.Label)} holds {Plural(h.Count, "connection")} while {h.Detail}. Open the connection after that call, or close it before: don't keep a pooled connection across a slow call that doesn't use it.",
                HolderKind.Thread => $"{Code(h.Label)} holds {Plural(h.Count, "connection")} on a running thread{(h.Detail is null ? "" : $" ({h.Detail})")}. Find out why that work isn't finishing.",
                HolderKind.Field => $"{Code(h.Label)} keeps {Plural(h.Count, "open connection")} in a field. Open connections where they're used and dispose them there, instead of storing them.",
                HolderKind.Leaked => $"{Plural(h.Count, "connection")} {(h.Count == 1 ? "was" : "were")} opened and never closed, and nothing references {(h.Count == 1 ? "it" : "them")} any more{(h.Detail is null ? "" : $" (last command: {h.Detail})")}. The pool gets a leaked connection back only after a garbage collection. Wrap every connection in `using`, so an early return or exception still closes it.",
                _ => $"{Plural(h.Count, "connection")} {(h.Count == 1 ? "has" : "have")} no owner the dump can name.",
            });
        }

        steps.Add($"Don't raise `Max Pool Size` to make this go away: it delays the same failure and moves the load to the database.");
        if (leaked == 0)
        {
            steps.Add("Restart the process to recover now, or wait for the holders above to finish.");
        }

        var evidence = new List<EntityRef> { EntityUri.ObjectRef(pool.Address, target is null ? "connection pool" : $"pool: {target}", $"{total} of {max} in use") };
        evidence.AddRange(holders.Where(h => h.Evidence is not null).Take(4).Select(h => h.Evidence!));
        evidence.AddRange(waiters.Take(4).Select(context.ThreadRef));

        var severity = waiting > 0 ? Severity.Critical : Severity.Warning;
        var confidence = waiting > 0 ? Confidence.High : Confidence.Medium;
        var title = waiting > 0
            ? $"Connection pool exhausted: all {max} connections to {where} are in use{waitingText}"
            : $"Connection pool for {where} is at its limit: all {max} connections are in use";
        var holderSummary = holders.Count == 0 ? "" : " " + Sentence(JoinAnd(holders.Take(3).Select(h => h.Kind == HolderKind.Leaked
            ? $"{h.Count} {(h.Count == 1 ? "was" : "were")} leaked (never closed)"
            : $"{h.Count} {(h.Count == 1 ? "is" : "are")} held by {Code(h.Label)}").ToList())) + ".";
        return new AnalyzerResult(new Finding(
            $"connection-pool.{Slug(target) ?? pool.Address.ToString("x")}", Name, severity, confidence,
            title,
            string.Join(" · ", why),
            meaning,
            steps,
            [Symptom.Hang, Symptom.SlowCpu],
            evidence,
            Scores.For(severity, confidence, Math.Min(1, (waiting + total) / 100.0))),
            VerdictHeadline: waiting > 0
                ? $"The app is waiting for database connections: all {max} pooled connections to {target ?? "the database"} are taken, and {Plural(waiting, "caller")} {(waiting == 1 ? "is" : "are")} queued for one."
                : $"All {max} pooled connections to {target ?? "the database"} are in use; the next Open() will wait.",
            VerdictBody: $"The connection pool is at `Max Pool Size` ({max}), so `Open()` waits until a connection is closed.{holderSummary}");
    }

    private enum HolderKind
    {
        Async,
        Thread,
        Field,
        Leaked,
        Unknown,
    }

    private sealed record HolderInfo(HolderKind Kind, string Label, string? Detail, EntityRef? Evidence);

    /// <summary>Who keeps a checked-out connection: the <c>SqlConnection</c> that owns it, and whatever keeps that alive.</summary>
    private static HolderInfo Holder(AnalysisContext context, ObjectExplorer explorer, ClrObject inner)
    {
        var index = explorer.Index;
        var innerIndex = index.IndexOf(inner.Address);
        if (innerIndex < 0)
        {
            return new HolderInfo(HolderKind.Unknown, "?", null, null);
        }

        // The owning connection is the referrer whose _innerConnection is this one. Referrers include
        // objects nothing roots any more, which is exactly what a leaked connection is.
        ClrObject owner = default;
        foreach (var r in index.ReferrersOf(innerIndex))
        {
            var candidate = context.Runtime.Heap.GetObject(index.Addresses[r], index.TypeOf(r));
            if (candidate.Type?.GetFieldByName("_innerConnection") is not null
                && candidate.TryReadObjectField("_innerConnection", out var pointsAt) && pointsAt.Address == inner.Address)
            {
                owner = candidate;
                break;
            }
        }

        if (owner.IsNull)
        {
            return new HolderInfo(HolderKind.Unknown, "no owner", null, EntityUri.ObjectRef(inner.Address, FrameClassifier.ShortTypeName(inner.Type?.Name)));
        }

        var ownerName = FrameClassifier.ShortTypeName(owner.Type?.Name);
        var thread = context.Threads.FirstOrDefault(t => t.Roots.Any(root => root.Address == owner.Address) && t.WhereIsApp);
        if (thread is not null)
        {
            return new HolderInfo(HolderKind.Thread, thread.WhereName ?? thread.Where ?? "?", thread.StateLabel, context.ThreadRef(thread));
        }

        if (AsyncHolder(context, index, owner) is { } box)
        {
            var method = AsyncStackWalker.MethodOf(box);
            var awaiting = AsyncStackWalker.DescribeAwaited(box);
            return new HolderInfo(HolderKind.Async, method, $"it is suspended, awaiting {(awaiting.StartsWith("a ", StringComparison.Ordinal) ? awaiting : Code(awaiting))}", EntityUri.ObjectRef(box.Address, method, $"suspended, holds a {ownerName}"));
        }

        var roots = explorer.FindRoots(new RootsQuery(owner.Address, RootPathMode.Shortest), CancellationToken.None);
        if (roots.Unrooted)
        {
            return new HolderInfo(HolderKind.Leaked, "leaked", LastCommand(context, explorer, owner), EntityUri.ObjectRef(owner.Address, ownerName, "leaked: open, unreferenced"));
        }

        if (DuplicateStringsAnalyzer.FieldHolding(explorer, owner.Address) is { } field)
        {
            return new HolderInfo(HolderKind.Field, field, null, EntityUri.ObjectRef(owner.Address, ownerName, $"held by {field}"));
        }

        return new HolderInfo(HolderKind.Unknown, roots.Paths.FirstOrDefault()?.Root.Label ?? "?", null, EntityUri.ObjectRef(owner.Address, ownerName));
    }

    /// <summary>
    /// The suspended application async method whose state machine holds <paramref name="owner"/> in a
    /// local: the box itself in Release builds (the state machine is a struct inside it), or a
    /// state-machine class the box points at in Debug builds.
    /// </summary>
    private static ClrObject? AsyncHolder(AnalysisContext context, HeapIndex index, ClrObject owner)
    {
        var ownerIndex = index.IndexOf(owner.Address);
        if (ownerIndex < 0)
        {
            return null;
        }

        foreach (var r in index.ReferrersOf(ownerIndex))
        {
            var name = index.TypeOf(r).Name ?? "";
            if (IsBox(name))
            {
                var box = context.Runtime.Heap.GetObject(index.Addresses[r], index.TypeOf(r));
                if (AsyncStackWalker.IsApp(box) && AsyncStackWalker.IsPending(box))
                {
                    return box;
                }
            }
            else if (name.Contains(">d__", StringComparison.Ordinal))
            {
                foreach (var b in index.ReferrersOf(r))
                {
                    if (IsBox(index.TypeOf(b).Name ?? ""))
                    {
                        var box = context.Runtime.Heap.GetObject(index.Addresses[b], index.TypeOf(b));
                        if (AsyncStackWalker.IsApp(box) && AsyncStackWalker.IsPending(box))
                        {
                            return box;
                        }
                    }
                }
            }
        }

        return null;

        static bool IsBox(string typeName) => typeName.Contains("StateMachineBox<", StringComparison.Ordinal);
    }

    /// <summary>The text of a command that used <paramref name="connection"/>: the best clue to the code that leaked it.</summary>
    private static string? LastCommand(AnalysisContext context, ObjectExplorer explorer, ClrObject connection)
    {
        var index = explorer.Index;
        var i = index.IndexOf(connection.Address);
        if (i < 0)
        {
            return null;
        }

        foreach (var r in index.ReferrersOf(i))
        {
            var command = context.Runtime.Heap.GetObject(index.Addresses[r], index.TypeOf(r));
            if (command.Type?.GetFieldByName("_commandText") is not null && command.ReadStringField("_commandText") is { Length: > 0 } text)
            {
                return Code(DuplicateStringsAnalyzer.Clip(text, 80));
            }
        }

        return null;
    }

    /// <summary>The connections the pool owns (checked out and idle), from its object list.</summary>
    private static List<ClrObject> Connections(ClrObject pool)
    {
        var list = new List<ClrObject>();
        if (!pool.TryReadObjectField("_objectList", out var objects) || objects.IsNull
            || !objects.TryReadObjectField("_items", out var items) || items.IsNull
            || !objects.TryReadField<int>("_size", out var size))
        {
            return list;
        }

        var array = items.AsArray();
        for (var i = 0; i < Math.Min(Math.Min(size, array.Length), MaxConnections); i++)
        {
            var item = array.GetObjectValue(i);
            if (!item.IsNull && item.IsValid)
            {
                list.Add(item);
            }
        }

        return list;
    }

    /// <summary>"Orders on sql01": the database and server, from a pooled connection or the pool's connection string. Never the password.</summary>
    private static string? Target(IReadOnlyList<ClrObject> connections, ClrObject pool)
    {
        foreach (var c in connections)
        {
            var server = c.Type?.GetFieldByName("<CurrentDataSource>k__BackingField") is null ? null : c.ReadStringField("<CurrentDataSource>k__BackingField");
            var database = c.Type?.GetFieldByName("<CurrentDatabase>k__BackingField") is null ? null : c.ReadStringField("<CurrentDatabase>k__BackingField");
            if (server is not null || database is not null)
            {
                return Describe(server, database);
            }
        }

        // No connection to read: parse the pool key's connection string for the server and database only.
        if (pool.TryReadObjectField("_connectionPoolGroup", out var group) && !group.IsNull
            && group.TryReadObjectField("_poolKey", out var key) && !key.IsNull
            && key.Type?.GetFieldByName("_connectionString") is not null
            && key.ReadStringField("_connectionString") is { } connectionString)
        {
            string? server = null, database = null;
            foreach (var part in connectionString.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var name = part[..eq].Trim().ToLowerInvariant();
                var value = part[(eq + 1)..].Trim();
                if (name is "server" or "data source" or "address" or "addr" or "network address")
                {
                    server = value;
                }
                else if (name is "database" or "initial catalog")
                {
                    database = value;
                }
            }

            return server is null && database is null ? null : Describe(server, database);
        }

        return null;
    }

    private static string Describe(string? server, string? database)
    {
        if (server is not null)
        {
            // tcp:host,port → host: the port varies more than it informs.
            server = server.Trim();
            var colon = server.IndexOf(':');
            if (colon > 0 && server[..colon] is "tcp" or "np" or "lpc" or "admin")
            {
                server = server[(colon + 1)..];
            }

            var comma = server.IndexOf(',');
            if (comma > 0)
            {
                server = server[..comma];
            }
        }

        return (database, server) switch
        {
            ({ Length: > 0 } d, { Length: > 0 } s) => $"{d} on {s}",
            ({ Length: > 0 } d, _) => d,
            (_, { Length: > 0 } s) => s,
            _ => "the database",
        };
    }

    /// <summary>A caller blocked in <c>Open()</c> waiting for the pool (not the pool's own worker that serves async opens).</summary>
    private static string? Slug(string? s) =>
        s is null ? null : string.Concat(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '-'));

    private static bool IsWaitingForPool(ThreadRecord thread) =>
        thread.Frames.Any(f => (f.FullName ?? f.Display).Contains("DbConnectionPool.TryGetConnection", StringComparison.Ordinal))
        && !thread.Frames.Any(f => (f.FullName ?? f.Display).Contains("DbConnectionPool.WaitForPendingOpen", StringComparison.Ordinal));
}
