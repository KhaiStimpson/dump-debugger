using DumpDebugger.Core.Contracts;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>The five-cell vitals strip under the verdict.</summary>
public static class VitalsBuilder
{
    public static IReadOnlyList<Vital> Build(AnalysisContext context, HeapOverview heap, ThreadPoolStats? pool)
    {
        var threads = context.Threads;
        var blocked = threads.Count(t => t.State == ThreadState.Blocked);
        var running = threads.Count(t => t.State == ThreadState.Running);
        var waitingApp = threads.Count(t => t.State == ThreadState.Waiting && t.WhereIsApp && t.Role != ThreadRole.Main);
        var vitals = new List<Vital>
        {
            new("threads", "Threads", Count(threads.Count), $"{blocked} blocked · " + (waitingApp > 0 ? $"{waitingApp} waiting · " : "") + $"{running} running",
                threads.Count == 0 ? 0 : (double)(blocked + waitingApp) / threads.Count, blocked > 0 ? Tone.Critical : waitingApp > 0 ? Tone.Warning : Tone.Neutral),
        };

        var workers = threads.Where(t => t.Role is ThreadRole.PoolWorker or ThreadRole.Request).ToList();
        var busy = workers.Count(t => t.State != ThreadState.Idle);
        var poolDetail = "workers busy" + (pool?.QueuedWorkItems is { } q ? $" · {Count(q)} queued" : "");
        vitals.Add(new Vital("threadPool", "Thread pool", workers.Count == 0 ? "—" : $"{busy}/{workers.Count}", poolDetail,
            workers.Count == 0 ? null : (double)busy / workers.Count,
            workers.Count > 0 && busy == workers.Count ? Tone.Warning : Tone.Neutral));

        var gen2 = heap.Generations.FirstOrDefault(g => g.Name == "Gen2")?.Bytes ?? 0;
        var loh = heap.Generations.FirstOrDefault(g => g.Name == "LOH")?.Bytes ?? 0;
        var segTotal = Math.Max(1, heap.Generations.Sum(g => g.Bytes));
        vitals.Add(new Vital("heap", "Managed heap", Bytes(heap.TotalBytes),
            $"LOH {Percent((double)loh / segTotal)} · Gen2 {Percent((double)gen2 / segTotal)}" + (heap.Walked ? "" : " · walking…"),
            (double)(loh + gen2) / segTotal, Tone.Accent));

        var requests = threads.Where(t => t.Request is not null).ToList();
        var stuck = requests.Count(t => t.State is ThreadState.Blocked or ThreadState.Waiting);
        vitals.Add(new Vital("requests", "Requests in flight", Count(requests.Count),
            requests.Count == 0 ? "none found on threads" : $"{stuck} stuck waiting",
            requests.Count == 0 ? 0 : (double)stuck / requests.Count, stuck > 0 ? Tone.Warning : Tone.Neutral));

        var exceptions = threads.Count(t => t.Exception is not null);
        vitals.Add(new Vital("exceptions", "Exceptions", Count(exceptions), "on thread stacks",
            Math.Min(1, exceptions / 10.0), exceptions > 0 ? Tone.Info : Tone.Neutral));

        return vitals;
    }
}

/// <summary>Thread-pool numbers that need the heap (queued work items); filled in by the analyzer pack.</summary>
public sealed record ThreadPoolStats(int? QueuedWorkItems, int? MinThreads, int? MaxThreads);
