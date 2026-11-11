using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Locks;
using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Analysis.Threads;

/// <summary>Thread list and thread detail queries, answered from the computed model (no ClrMD access).</summary>
public static class ThreadQueries
{
    public static ThreadGroupsResult Groups(IReadOnlyList<ThreadRecord> threads, ThreadGroupsQuery query)
    {
        var counts = new List<ThreadStateCount>
        {
            new(ThreadStateFilter.All, threads.Count),
            new(ThreadStateFilter.Blocked, threads.Count(t => t.State == ThreadState.Blocked)),
            new(ThreadStateFilter.Waiting, threads.Count(t => t.State == ThreadState.Waiting)),
            new(ThreadStateFilter.Running, threads.Count(t => t.State == ThreadState.Running)),
            new(ThreadStateFilter.Idle, threads.Count(t => t.State == ThreadState.Idle)),
        };

        var filtered = threads.Where(t => Matches(t, query)).ToList();
        IEnumerable<IGrouping<string, ThreadRecord>> grouped = query.GroupBy switch
        {
            ThreadGrouping.Request => filtered.GroupBy(t => t.Request is { } r ? $"request:{r}:{t.StateLabel}" : t.GroupKey),
            ThreadGrouping.Flat => filtered.GroupBy(t => $"thread:{t.Id}"),
            _ => filtered.GroupBy(t => t.GroupKey),
        };

        var groups = grouped.Select(g => ToGroup(g.Key, g.ToList()))
            .OrderByDescending(g => g.InDeadlock)
            .ThenByDescending(g => g.WhereIsApp)
            .ThenBy(g => StateRank(g.State))
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.ThreadIds[0])
            .ToList();

        return new ThreadGroupsResult(groups, threads.Count, counts);
    }

    private static ThreadGroup ToGroup(string key, List<ThreadRecord> members)
    {
        var first = members[0];
        var requests = members.Select(m => m.Request).Distinct().ToList();
        var request = requests.Count == 1 ? requests[0] : requests.Count(r => r is not null) > 1 ? $"{requests.Count(r => r is not null)} different requests" : requests.FirstOrDefault(r => r is not null);
        var labels = members.Select(m => m.StateLabel).Distinct().ToList();
        var where = members.Count == 1 || members.All(m => m.Where == first.Where) ? first.Where ?? "?" : first.Where + " …";
        return new ThreadGroup(
            key,
            members.Count,
            where,
            first.WhereIsApp,
            request,
            members.OrderBy(m => StateRank(m.State)).First().State,
            labels.Count == 1 ? labels[0] : first.StateLabel,
            members.Select(m => m.Id).ToList(),
            members.Any(m => m.InDeadlock));
    }

    private static int StateRank(ThreadState s) => s switch
    {
        ThreadState.Blocked => 0,
        ThreadState.Waiting => 1,
        ThreadState.Running => 2,
        _ => 3,
    };

    private static bool Matches(ThreadRecord t, ThreadGroupsQuery q)
    {
        var stateOk = q.State switch
        {
            ThreadStateFilter.Blocked => t.State == ThreadState.Blocked,
            ThreadStateFilter.Waiting => t.State == ThreadState.Waiting,
            ThreadStateFilter.Running => t.State == ThreadState.Running,
            ThreadStateFilter.Idle => t.State == ThreadState.Idle,
            _ => true,
        };
        if (!stateOk)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(q.Search))
        {
            return true;
        }

        var s = q.Search.Trim().TrimStart('#');
        return t.Id.ToString() == s
               || (t.Where?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
               || (t.Request?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false)
               || t.Frames.Any(f => f.Display.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    public static ThreadDetail Detail(ThreadRecord t, IReadOnlyList<ThreadRecord> threads, LockModel locks)
    {
        var holds = t.Holds.Where(locks.Locks.ContainsKey).Select(h => locks.Locks[h].Ref).ToList();
        EntityRef? waitingFor = t.WaitingOnLock is { } w && locks.Locks.TryGetValue(w, out var wl) ? wl.Ref : null;
        EntityRef? owner = t.WaitingOnThread is { } o ? EntityUri.ThreadRef(o, threads.FirstOrDefault(x => x.Id == o)?.Request) : null;

        var lockAddresses = locks.Locks.Keys.ToHashSet();
        var stackObjects = t.Roots
            .Where(r => r.IsAppType || (r.TypeName?.Contains("HttpContext", StringComparison.Ordinal) ?? false))
            .Where(r => !lockAddresses.Contains(r.Address))
            .Take(12)
            .Select(r => EntityUri.ObjectRef(r.Address, FrameClassifier.ShortTypeName(r.TypeName), r.TypeName))
            .ToList();

        var same = threads.Where(x => x.Id != t.Id && x.GroupKey == t.GroupKey).Select(x => x.Id).ToList();
        return new ThreadDetail(t.ToSummary(), t.Frames, holds, waitingFor, owner, stackObjects, same);
    }
}
