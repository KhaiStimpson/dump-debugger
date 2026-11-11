using System.Diagnostics;
using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Locks;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Heap;

/// <summary>
/// Object explorer queries over a <see cref="HeapIndex"/>: inspect, referrers, instances,
/// owner-aware large-object names, and GC-root paths. Root paths are a backwards BFS over the
/// reverse-reference graph from the target until something that is a root (a static field's
/// value, a stack local, a handle) is reached, with a depth cap, a visit cap and cancellation.
/// </summary>
public sealed class ObjectExplorer
{
    private const int MaxDepth = 64;
    private const int MaxVisits = 4_000_000;

    private readonly ClrRuntime _runtime;
    private readonly HeapIndex _index;
    private Dictionary<int, RootInfo>? _roots;
    private Dictionary<ulong, string>? _statics;
    private int[]? _parent;
    private int[]? _stamp;
    private int _generation;

    public ObjectExplorer(ClrRuntime runtime, HeapIndex index)
    {
        _runtime = runtime;
        _index = index;
    }

    public HeapIndex Index => _index;

    public ObjectView Inspect(ObjectQuery q)
    {
        var obj = _runtime.Heap.GetObject(q.Address);
        if (!obj.IsValid || obj.Type is null)
        {
            throw new Core.Rpc.RpcException(new Core.Rpc.RpcError(Core.Rpc.RpcError.InvalidParams, $"0x{q.Address:x} is not the start of a live object."));
        }

        var i = _index.IndexOf(q.Address);
        var type = obj.Type;
        string? stringValue = type.IsString ? obj.AsString(4096) : null;
        int? length = type.IsArray ? obj.AsArray().Length : type.IsString ? stringValue?.Length : null;
        string? preview = type.IsArray && type.ComponentType?.ElementType == ClrElementType.UInt8 ? ObjectDescriber.HexPreview(ObjectDescriber.ReadBytes(obj, 16)) : null;
        // Byte arrays get the hex/ASCII preview instead of one row per byte.
        var elements = type.IsArray && type.ComponentType?.ElementType != ClrElementType.UInt8
            ? ObjectDescriber.Elements(obj, q.ElementOffset, Math.Clamp(q.ElementLimit, 1, 500))
            : null;

        return new ObjectView(
            EntityUri.Object(q.Address),
            q.Address,
            type.Name ?? "?",
            (long)obj.Size,
            i >= 0 ? HeapIndex.GenerationName(_index.Generations[i]) : "?",
            i >= 0 ? OwnerAwareIdentity(i) : ObjectDescriber.Identity(obj),
            stringValue,
            length,
            preview,
            type.IsArray || type.IsString ? [] : ObjectDescriber.Fields(obj),
            elements,
            q.ElementOffset,
            obj.IsFree);
    }

    public Page<Referrer> Referrers(ReferrersQuery q)
    {
        var i = RequireIndex(q.Address);
        var sources = _index.ReferrersOf(i).ToArray();
        var page = sources.Skip(q.Offset).Take(Math.Clamp(q.Limit, 1, 200)).Select(s =>
        {
            var obj = _runtime.Heap.GetObject(_index.Addresses[s]);
            return new Referrer(
                EntityUri.ObjectRef(obj.Address, ObjectDescriber.Identity(obj), obj.Type?.Name),
                FrameClassifier.ShortTypeName(obj.Type?.Name),
                FieldPointingAt(obj, q.Address));
        }).ToList();
        return new Page<Referrer>(page, sources.Length, q.Offset);
    }

    public Page<InstanceRow> Instances(InstancesQuery q)
    {
        var typeIds = _index.Types.Select((t, id) => (t, id)).Where(x => x.t.Name == q.TypeName).Select(x => x.id).ToHashSet();
        var matches = new List<int>();
        for (var i = 0; i < _index.Count; i++)
        {
            if (typeIds.Contains(_index.TypeIds[i]))
            {
                matches.Add(i);
            }
        }

        var page = matches.OrderByDescending(i => _index.Sizes[i]).Skip(q.Offset).Take(Math.Clamp(q.Limit, 1, 500))
            .Select(i => new InstanceRow(
                EntityUri.Object(_index.Addresses[i]),
                _index.Addresses[i],
                _index.Sizes[i],
                HeapIndex.GenerationName(_index.Generations[i]),
                ObjectDescriber.Identity(_runtime.Heap.GetObject(_index.Addresses[i]))))
            .ToList();
        return new Page<InstanceRow>(page, matches.Count, q.Offset);
    }

    public IReadOnlyList<LargeObject> LargeObjects(int max = 40)
    {
        var top = new PriorityQueue<int, long>();
        for (var i = 0; i < _index.Count; i++)
        {
            if (_index.Sizes[i] < 85_000)
            {
                continue;
            }

            top.Enqueue(i, _index.Sizes[i]);
            if (top.Count > max)
            {
                top.Dequeue();
            }
        }

        var list = new List<int>();
        while (top.Count > 0)
        {
            list.Add(top.Dequeue());
        }

        list.Reverse();
        var pinned = Roots().Where(r => r.Value.Kind is RootKind.PinnedHandle or RootKind.AsyncPinned).Select(r => r.Key).ToHashSet();
        return list.Select(i =>
        {
            var obj = _runtime.Heap.GetObject(_index.Addresses[i]);
            var owner = FirstAppReferrer(i, 4);
            var detail = owner is { } o ? $"{FrameClassifier.ShortTypeName(_index.TypeOf(o.Index).Name)}.{o.Field}" : null;
            return new LargeObject(
                EntityUri.Object(obj.Address),
                obj.Address,
                obj.Type?.Name ?? "?",
                _index.Sizes[i],
                HeapIndex.GenerationName(_index.Generations[i]),
                OwnerAwareIdentity(i),
                detail,
                pinned.Contains(i));
        }).ToList();
    }

    /// <summary>
    /// "PDF document "Q3-statements" in ReportCache": what the object is (by content or its own
    /// identifying field), named by the app object that owns it, and the app type or static
    /// that holds that owner. Collections in between (arrays, dictionary entries) are skipped.
    /// </summary>
    public string OwnerAwareIdentity(int i)
    {
        var obj = _runtime.Heap.GetObject(_index.Addresses[i]);
        var type = obj.Type;
        var isBytes = type?.IsArray == true && type.ComponentType?.ElementType == ClrElementType.UInt8;
        var what = isBytes ? ObjectDescriber.SniffBytes(obj) : null;
        var own = ObjectDescriber.IdentifyingString(obj);
        var owner = FirstAppReferrer(i, 4);

        string? key = own;
        if (key is null && owner is { } o)
        {
            key = ObjectDescriber.IdentifyingString(_runtime.Heap.GetObject(_index.Addresses[o.Index]));
        }

        what ??= FrameClassifier.ShortTypeName(type?.Name);
        var holder = owner is { } ow ? Holder(ow.Index, _index.TypeIds[ow.Index]) : Holder(i, _index.TypeIds[i]);
        var name = key is null ? what : $"{what} \"{key}\"";
        if (holder is not null)
        {
            return $"{name} in {holder}";
        }

        return owner is { } x && key is null ? $"{name} in {FrameClassifier.ShortTypeName(_index.TypeOf(x.Index).Name)}" : name;
    }

    /// <summary>The nearest application-type object that references <paramref name="i"/> (through framework collections), and the field it uses.</summary>
    private (int Index, string Field)? FirstAppReferrer(int i, int maxHops)
    {
        var frontier = new List<int> { i };
        var seen = new HashSet<int> { i };
        for (var hop = 0; hop < maxHops && frontier.Count > 0; hop++)
        {
            var next = new List<int>();
            foreach (var cur in frontier)
            {
                foreach (var s in _index.ReferrersOf(cur))
                {
                    if (!seen.Add(s))
                    {
                        continue;
                    }

                    var t = _index.TypeOf(s);
                    if (FrameClassifier.IsAppType(t.Name, t.Module))
                    {
                        return (s, FieldPointingAt(_runtime.Heap.GetObject(_index.Addresses[s]), _index.Addresses[cur]));
                    }

                    if (next.Count < 64)
                    {
                        next.Add(s);
                    }
                }
            }

            frontier = next;
        }

        return null;
    }

    /// <summary>Who holds the owner: a static field's declaring type, or an app object of a different type, within a few hops.</summary>
    private string? Holder(int start, int ownerTypeId)
    {
        Statics();
        var frontier = new List<int> { start };
        var seen = new HashSet<int> { start };
        for (var hop = 0; hop < 6 && frontier.Count > 0; hop++)
        {
            var next = new List<int>();
            foreach (var cur in frontier)
            {
                if (_statics!.TryGetValue(_index.Addresses[cur], out var label))
                {
                    return label[..label.LastIndexOf('.')];
                }

                foreach (var s in _index.ReferrersOf(cur))
                {
                    if (!seen.Add(s))
                    {
                        continue;
                    }

                    var t = _index.TypeOf(s);
                    if (_index.TypeIds[s] != ownerTypeId && FrameClassifier.IsAppType(t.Name, t.Module))
                    {
                        return FrameClassifier.ShortTypeName(t.Name);
                    }

                    if (next.Count < 64)
                    {
                        next.Add(s);
                    }
                }
            }

            frontier = next;
        }

        return null;
    }

    public RootPathsResult FindRoots(RootsQuery q, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var target = RequireIndex(q.Address);
        var roots = Roots();
        var statics = Statics();
        var n = _index.Count;
        _parent ??= new int[n];
        _stamp ??= new int[n];
        var gen = ++_generation;

        var paths = new List<RootPath>();
        var maxPaths = q.Mode == RootPathMode.Shortest ? 1 : Math.Clamp(q.MaxPaths, 1, 50);
        var queue = new Queue<(int Node, int Depth)>();
        queue.Enqueue((target, 0));
        _stamp[target] = gen;
        _parent[target] = -1;
        var visits = 0;
        var truncated = false;

        while (queue.Count > 0 && paths.Count < maxPaths)
        {
            var (cur, depth) = queue.Dequeue();
            if (++visits % 4096 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var root = RootOf(cur, roots, statics);
            if (root is not null)
            {
                paths.Add(BuildPath(root, cur));
                if (cur != target)
                {
                    continue; // a root is an endpoint; don't walk past it
                }
            }

            if (depth >= MaxDepth || visits >= MaxVisits)
            {
                truncated = true;
                continue;
            }

            foreach (var s in _index.ReferrersOf(cur))
            {
                if (_stamp[s] == gen)
                {
                    continue;
                }

                _stamp[s] = gen;
                _parent[s] = cur;
                queue.Enqueue((s, depth + 1));
            }
        }

        var unrooted = paths.Count == 0 && !truncated;
        return new RootPathsResult(paths, truncated, unrooted, sw.Elapsed.TotalSeconds, Explain(paths, unrooted, truncated));
    }

    private RootPath BuildPath(RootInfo root, int rootNode)
    {
        var steps = new List<RootPathStep>();
        for (var cur = rootNode; cur >= 0; cur = _parent![cur])
        {
            var obj = _runtime.Heap.GetObject(_index.Addresses[cur]);
            var next = _parent[cur];
            var via = next >= 0 ? FieldPointingAt(obj, _index.Addresses[next]) : null;
            steps.Add(new RootPathStep(
                EntityUri.ObjectRef(obj.Address, ObjectDescriber.Identity(obj), obj.Type?.Name),
                obj.Type?.Name ?? "?",
                via,
                HeapIndex.GenerationName(_index.Generations[cur]) is "LOH" ? "LOH" : null));
            if (steps.Count > MaxDepth + 2)
            {
                break;
            }
        }

        return new RootPath(root, steps);
    }

    private static string Explain(IReadOnlyList<RootPath> paths, bool unrooted, bool truncated)
    {
        if (unrooted)
        {
            return "Nothing roots this object: it's garbage the GC simply hadn't collected when the dump was taken. It isn't what's holding memory.";
        }

        if (paths.Count == 0)
        {
            return "No root found within the search limits. The object is referenced through a very long or very wide chain; try a referrer instead.";
        }

        var root = paths[0].Root;
        var text = root.Kind switch
        {
            RootKind.Static => $"A static field ({Code(root.Label.Replace("static ", ""))}) keeps this alive for the life of the process. Unless something removes it from there, it will never be collected.",
            RootKind.Stack => $"A local variable ({root.Detail}) keeps this alive until that method returns.",
            RootKind.PinnedHandle or RootKind.AsyncPinned => "A pinned GC handle keeps this alive and fixed in place (interop, I/O buffers, or the storage behind static fields). It stays until the handle is freed.",
            RootKind.StrongHandle or RootKind.RefCounted => "A strong GC handle keeps this alive (often a timer, an event subscription from native code, or a cache held by the runtime). It stays until the handle is freed.",
            RootKind.FinalizerQueue => "It's only alive because it's queued for finalization. Once the finalizer thread runs it, it can be collected; if the finalizer thread is stuck, these pile up.",
            _ => "A GC root keeps this alive.",
        };
        return truncated ? text + " (The search hit its limits, so other paths may exist.)" : text;
    }

    private RootInfo? RootOf(int i, Dictionary<int, RootInfo> roots, Dictionary<ulong, string> statics)
    {
        if (statics.TryGetValue(_index.Addresses[i], out var label))
        {
            return new RootInfo(RootKind.Static, $"static {label}", "static field", null);
        }

        return roots.GetValueOrDefault(i);
    }

    public Dictionary<int, RootInfo> Roots()
    {
        if (_roots is not null)
        {
            return _roots;
        }

        var map = new Dictionary<int, RootInfo>();
        foreach (var root in _runtime.Heap.EnumerateRoots())
        {
            var i = _index.IndexOf(root.Object.Address);
            if (i < 0 || map.ContainsKey(i) && root.RootKind != ClrRootKind.Stack)
            {
                continue;
            }

            RootInfo info = root.RootKind switch
            {
                ClrRootKind.Stack when root is ClrStackRoot sr => StackRoot(sr),
                ClrRootKind.Stack => new RootInfo(RootKind.Stack, "stack local", "a local variable", null),
                ClrRootKind.StaticVar => new RootInfo(RootKind.Static, "static field", "static field", null),
                ClrRootKind.ThreadStaticVar => new RootInfo(RootKind.ThreadStatic, "thread-static field", "thread static", null),
                ClrRootKind.PinnedHandle => new RootInfo(RootKind.PinnedHandle, "pinned handle", "GC handle (pinned)", null),
                ClrRootKind.AsyncPinnedHandle => new RootInfo(RootKind.AsyncPinned, "async pinned handle", "overlapped I/O buffer", null),
                ClrRootKind.StrongHandle or ClrRootKind.SizedRefHandle => new RootInfo(RootKind.StrongHandle, "strong handle", "GC handle (strong)", null),
                ClrRootKind.RefCountedHandle => new RootInfo(RootKind.RefCounted, "ref-counted handle", "COM/WinRT reference", null),
                ClrRootKind.FinalizerQueue => new RootInfo(RootKind.FinalizerQueue, "finalizer queue", "waiting to be finalized", null),
                _ => new RootInfo(RootKind.Other, root.RootKind.ToString(), "GC root", null),
            };

            if (!map.TryGetValue(i, out var existing) || Rank(info.Kind) < Rank(existing.Kind))
            {
                map[i] = info;
            }
        }

        return _roots = map;
    }

    private static int Rank(RootKind k) => k switch
    {
        RootKind.Static => 0,
        RootKind.Stack => 1,
        RootKind.ThreadStatic => 2,
        RootKind.StrongHandle or RootKind.PinnedHandle or RootKind.AsyncPinned or RootKind.RefCounted => 3,
        _ => 4,
    };

    private static RootInfo StackRoot(ClrStackRoot sr)
    {
        var thread = sr.StackFrame?.Thread;
        var method = sr.StackFrame?.Method;
        var where = method is null ? "a runtime frame" : FrameClassifier.DisplayName(method);
        var id = thread?.ManagedThreadId;
        return new RootInfo(RootKind.Stack, $"local in {where}", $"a local in {Code(where)} on thread #{id}", id);
    }

    /// <summary>Static reference fields across loaded types: value address → "Type.field".</summary>
    public Dictionary<ulong, string> Statics()
    {
        if (_statics is not null)
        {
            return _statics;
        }

        var map = new Dictionary<ulong, string>();
        var domains = _runtime.AppDomains.ToList();
        foreach (var module in _runtime.EnumerateModules())
        {
            IEnumerable<(ulong MethodTable, int Token)> types;
            try
            {
                types = module.EnumerateTypeDefToMethodTableMap().ToList();
            }
            catch
            {
                continue;
            }

            foreach (var (mt, _) in types)
            {
                var type = _runtime.GetTypeByMethodTable(mt);
                if (type is null)
                {
                    continue;
                }

                foreach (var sf in type.StaticFields)
                {
                    if (!sf.IsObjectReference)
                    {
                        continue;
                    }

                    foreach (var domain in domains)
                    {
                        try
                        {
                            var value = sf.ReadObject(domain);
                            if (!value.IsNull)
                            {
                                map.TryAdd(value.Address, $"{FrameClassifier.ShortTypeName(type.Name)}.{LockModelBuilder.CleanFieldName(sf.Name)}");
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        return _statics = map;
    }

    /// <summary>The field (or array slot) of <paramref name="parent"/> that points at <paramref name="child"/>.</summary>
    public static string FieldPointingAt(ClrObject parent, ulong child)
    {
        try
        {
            // Big reference arrays: scan raw memory for the pointer instead of enumerating
            // millions of ClrMD references (an Order[4_000_000] took seconds the other way).
            if (parent.Type is { IsArray: true, ComponentType.IsObjectReference: true } arrayType)
            {
                var index = FindPointerInArray(parent, arrayType, child);
                if (index >= 0)
                {
                    return $"[{index}]";
                }
            }

            foreach (var r in parent.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: false))
            {
                if (r.Object.Address != child)
                {
                    continue;
                }

                var inner = r.InnerField is { Field: { } innerField } ? "." + LockModelBuilder.CleanFieldName(innerField.Name) : "";
                if (r.IsArrayElement && parent.Type?.ComponentSize is > 0 and var size)
                {
                    var pointerSize = parent.Type.Heap.Runtime.DataTarget.DataReader.PointerSize;
                    var index = (r.Offset - (2 * pointerSize)) / size;
                    return $"[{index}]{inner}";
                }

                if (r.Field is { } field)
                {
                    return LockModelBuilder.CleanFieldName(field.Name) + inner;
                }

                return inner.TrimStart('.');
            }
        }
        catch
        {
        }

        return "?";
    }

    private static long FindPointerInArray(ClrObject array, ClrType type, ulong target)
    {
        var reader = type.Heap.Runtime.DataTarget.DataReader;
        var pointerSize = reader.PointerSize;
        var length = array.AsArray().Length;
        var data = array.Address + (ulong)(2 * pointerSize);
        const int chunk = 64 * 1024;
        var buffer = new byte[chunk * pointerSize];
        for (long start = 0; start < length; start += chunk)
        {
            var n = (int)Math.Min(chunk, length - start);
            var span = buffer.AsSpan(0, n * pointerSize);
            if (reader.Read(data + (ulong)(start * pointerSize), span) != span.Length)
            {
                return -1;
            }

            for (var i = 0; i < n; i++)
            {
                var value = pointerSize == 8 ? BitConverter.ToUInt64(span.Slice(i * 8, 8)) : BitConverter.ToUInt32(span.Slice(i * 4, 4));
                if (value == target)
                {
                    return start + i;
                }
            }
        }

        return -1;
    }

    private int RequireIndex(ulong address)
    {
        var i = _index.IndexOf(address);
        return i >= 0
            ? i
            : throw new Core.Rpc.RpcException(new Core.Rpc.RpcError(Core.Rpc.RpcError.InvalidParams, $"0x{address:x} is not a live object in this dump."));
    }
}
