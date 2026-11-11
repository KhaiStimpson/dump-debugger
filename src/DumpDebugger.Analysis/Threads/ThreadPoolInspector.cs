using DumpDebugger.Analysis.Findings;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Threads;

/// <summary>
/// Reads the managed thread pool's queues (.NET Core portable pool): the global
/// <c>ThreadPoolWorkQueue.workItems</c> (plus high/low priority and assignable queues) and every
/// thread's local work-stealing queue. Counts come from each ConcurrentQueue segment's head and
/// tail indexes, so nothing is enumerated item by item. Returns null where the layout isn't
/// recognised (.NET Framework, Windows thread pool), and the vitals then say "unknown".
/// </summary>
public static class ThreadPoolInspector
{
    public static ThreadPoolStats Read(ClrRuntime runtime)
    {
        var pool = runtime.ThreadPool;
        int? queued = null;
        try
        {
            queued = CountQueued(runtime);
        }
        catch
        {
            // Unknown layout: leave it out rather than guess.
        }

        return new ThreadPoolStats(queued, pool?.MinThreads, pool?.MaxThreads);
    }

    private static int? CountQueued(ClrRuntime runtime)
    {
        var poolType = runtime.BaseClassLibrary.GetTypeByName("System.Threading.ThreadPool");
        var field = poolType?.GetStaticFieldByName("s_workQueue");
        if (field is null)
        {
            return null;
        }

        ClrObject queue = default;
        foreach (var domain in runtime.AppDomains)
        {
            queue = field.ReadObject(domain);
            if (!queue.IsNull)
            {
                break;
            }
        }

        if (queue.IsNull || !queue.IsValid)
        {
            return null;
        }

        var total = 0;
        foreach (var name in new[] { "workItems", "highPriorityWorkItems", "lowPriorityWorkItems" })
        {
            if (queue.TryReadObjectField(name, out var q) && !q.IsNull)
            {
                total += ConcurrentQueueCount(q);
            }
        }

        if (queue.TryReadObjectField("_assignableWorkItemQueues", out var assignable) && assignable is { IsNull: false, IsArray: true })
        {
            var array = assignable.AsArray();
            for (var i = 0; i < array.Length; i++)
            {
                var item = ReadArrayElement(assignable, i);
                if (!item.IsNull)
                {
                    total += ConcurrentQueueCount(item);
                }
            }
        }

        // Per-thread local queues: WorkStealingQueueList._queues.
        var listType = runtime.BaseClassLibrary.GetTypeByName("System.Threading.ThreadPoolWorkQueue+WorkStealingQueueList");
        var queuesField = listType?.GetStaticFieldByName("_queues");
        if (queuesField is not null)
        {
            foreach (var domain in runtime.AppDomains)
            {
                var locals = queuesField.ReadObject(domain);
                if (locals is not { IsNull: false, IsArray: true })
                {
                    continue;
                }

                var array = locals.AsArray();
                for (var i = 0; i < array.Length; i++)
                {
                    var wsq = ReadArrayElement(locals, i);
                    if (!wsq.IsNull)
                    {
                        total += Math.Max(0, wsq.ReadField<int>("m_tailIndex") - wsq.ReadField<int>("m_headIndex"));
                    }
                }

                break;
            }
        }

        return total;
    }

    private static int ConcurrentQueueCount(ClrObject queue)
    {
        var count = 0;
        if (!queue.TryReadObjectField("_head", out var segment))
        {
            return 0;
        }

        for (var hops = 0; !segment.IsNull && hops < 10_000; hops++)
        {
            var slots = segment.ReadObjectField("_slots");
            var length = slots.IsNull ? 0 : slots.AsArray().Length;
            var ht = segment.Type!.GetFieldByName("_headAndTail")!.ReadStruct(segment.Address, false);
            var head = ht.ReadField<int>("Head");
            var tail = ht.ReadField<int>("Tail");
            if (segment.ReadField<bool>("_frozenForEnqueues"))
            {
                tail -= length * 2; // FreezeOffset
            }

            count += Math.Clamp(tail - head, 0, length);
            segment = segment.ReadObjectField("_nextSegment");
        }

        return count;
    }

    private static ClrObject ReadArrayElement(ClrObject array, int index)
    {
        var reader = array.Type!.Heap.Runtime.DataTarget.DataReader;
        var at = array.Address + (ulong)(2 * reader.PointerSize) + (ulong)(index * reader.PointerSize);
        var target = reader.ReadPointer(at);
        return target == 0 ? default : array.Type.Heap.GetObject(target);
    }
}
