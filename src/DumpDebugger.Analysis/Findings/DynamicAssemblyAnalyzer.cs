using DumpDebugger.Analysis.Frames;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;
using static DumpDebugger.Analysis.Findings.Text;

namespace DumpDebugger.Analysis.Findings;

/// <summary>
/// Assembly leaks, which live outside the managed heap and so show up nowhere else: hundreds
/// of assemblies generated at run time (an <c>XmlSerializer</c> built per call is the classic),
/// and the same assembly loaded over and over into load contexts that never unload.
/// </summary>
public sealed class DynamicAssemblyAnalyzer : IFindingAnalyzer
{
    private const int MinDynamic = 50;
    private const int WarnDynamic = 200;
    private const int MinCopies = 10;
    private const int WarnCopies = 50;
    private const int SampledModules = 400;
    private const string XmlSerializerNamespace = "Microsoft.Xml.Serialization.GeneratedAssembly.";

    public string Name => "assemblies";

    public AnalyzerStage Stage => AnalyzerStage.Threads;

    public IEnumerable<AnalyzerResult> Analyze(AnalysisContext context)
    {
        var modules = context.Runtime.EnumerateModules().ToList();
        var dynamic = modules.Where(m => m.IsDynamic).ToList();
        if (dynamic.Count >= MinDynamic)
        {
            yield return Generated(context.Runtime, modules.Count, dynamic);
        }

        var copies = modules.Where(m => !m.IsDynamic && !string.IsNullOrEmpty(m.Name))
            .GroupBy(m => Path.GetFileName(m.Name!), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= MinCopies)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (copies.Count > 0)
        {
            yield return Duplicated(copies[0].Key, copies[0].Count(), copies[0].Sum(m => (long)m.Size), copies.Count - 1);
        }
    }

    private AnalyzerResult Generated(ClrRuntime runtime, int total, List<ClrModule> dynamic)
    {
        // What a generated assembly is for shows in the types it defines.
        var firstTypes = new Dictionary<string, int>();
        var serialized = new Dictionary<string, int>();
        var sampled = 0;
        var xml = 0;
        foreach (var module in dynamic.Take(SampledModules))
        {
            sampled++;
            var names = TypeNames(runtime, module);
            var writer = names.FirstOrDefault(n => n.StartsWith(XmlSerializerNamespace + "XmlSerializationWriter", StringComparison.Ordinal));
            if (writer is not null)
            {
                xml++;
                var target = writer[(XmlSerializerNamespace.Length + "XmlSerializationWriter".Length)..];
                serialized[target] = serialized.GetValueOrDefault(target) + 1;
            }
            else if (names.FirstOrDefault() is { } first)
            {
                firstTypes[first] = firstTypes.GetValueOrDefault(first) + 1;
            }
        }

        var n = dynamic.Count;
        var severity = n >= WarnDynamic ? Severity.Warning : Severity.Info;
        var share = $"{Count(n)} of the {Count(total)} loaded modules were generated at run time";
        var score = Scores.For(severity, Confidence.High, Math.Min(1, n / 1000.0));
        const string Unloadable = "Code generated at run time lives in a dynamic assembly, and an ordinary dynamic assembly is never unloaded: its types, metadata and compiled code stay for the life of the process. That memory is outside the managed heap, so the heap looks healthy while the process keeps growing.";

        if (xml >= sampled / 2 && serialized.Count > 0)
        {
            var top = serialized.OrderByDescending(kv => kv.Value).First();
            return new AnalyzerResult(new Finding(
                "assemblies.dynamic", Name, severity, Confidence.High,
                $"{Count(n)} assemblies were generated at run time, mostly by `XmlSerializer` for {Code(top.Key)}",
                share + (serialized.Count > 1 ? $" · serializers for {Plural(serialized.Count, "type")}" : $" · each one a serializer for {Code(top.Key)}"),
                Unloadable + " `XmlSerializer` generates a new serialization assembly every time it is constructed, except through its two simplest constructors, `XmlSerializer(Type)` and `XmlSerializer(Type, string)`, which are cached. The overloads that take an `XmlRootAttribute`, extra types or overrides are not, so creating one per call leaks an assembly per call and pays to generate and compile it each time.",
                [
                    $"Create the serializer for {Code(top.Key)} once and reuse it: a `static readonly` field, or a `ConcurrentDictionary` keyed by type and root name.",
                    "Search for `new XmlSerializer(` with more than one argument: those are the constructors that don't cache.",
                    "To confirm the fix, watch the assembly count (`dotnet-counters`, \"Number of Assemblies Loaded\"): it should stop climbing.",
                ],
                [Symptom.HighMemory, Symptom.SlowCpu],
                [EntityUri.TypeRef("System.Xml.Serialization.XmlSerializer", "XmlSerializer", "generates an assembly per instance")],
                score),
                VerdictHeadline: $"{Count(n)} assemblies were generated at run time, mostly by XmlSerializer for {top.Key}.",
                VerdictBody: $"An `XmlSerializer` for {Code(top.Key)} is being created per call through a constructor that doesn't cache, and each one generates an assembly that is never unloaded. That memory is outside the managed heap, so the heap looks healthy while the process keeps growing.");
        }

        var sample = firstTypes.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        var proxies = sample?.StartsWith("Castle.Proxies.", StringComparison.Ordinal) == true;
        return new AnalyzerResult(new Finding(
            "assemblies.dynamic", Name, severity, Confidence.High,
            $"{Count(n)} assemblies were generated at run time" + (sample is null ? "" : $", most of them defining {Code(FrameClassifier.ShortTypeName(sample))}"),
            share,
            Unloadable + " A steadily growing count means something generates code per call instead of once: a proxy generator or mocking library created each time, a serializer or mapper that emits code and isn't cached, or scripts and expressions compiled to a new assembly for every evaluation.",
            [
                proxies
                    ? "Share one `ProxyGenerator` for the whole app: each new generator gets its own assembly and can't reuse the proxies of the others."
                    : sample is null
                        ? "Find what generates code at run time (Reflection.Emit, a proxy or serializer library, a scripting engine) and make it reuse its result."
                        : $"Find what generates {Code(FrameClassifier.ShortTypeName(sample))} and cache it, so the code is generated once per shape instead of once per call.",
                "If the code really must be regenerated, emit it into a collectible assembly (`AssemblyBuilderAccess.RunAndCollect`, or a collectible `AssemblyLoadContext`) so it can be unloaded.",
                "To confirm the fix, watch the assembly count (`dotnet-counters`, \"Number of Assemblies Loaded\"): it should stop climbing.",
            ],
            [Symptom.HighMemory, Symptom.SlowCpu],
            sample is null ? [] : [EntityUri.TypeRef(sample, FrameClassifier.ShortTypeName(sample), "defined in the generated assemblies")],
            score),
            VerdictHeadline: $"{Count(n)} assemblies were generated at run time and can never be unloaded.",
            VerdictBody: "Something generates code per call instead of once. That memory is outside the managed heap, so the heap looks healthy while the process keeps growing.");
    }

    private AnalyzerResult Duplicated(string file, int copies, long bytes, int others)
    {
        var severity = copies >= WarnCopies ? Severity.Warning : Severity.Info;
        var name = Path.GetFileNameWithoutExtension(file);
        return new AnalyzerResult(new Finding(
            $"assemblies.copies.{name}", Name, severity, Confidence.High,
            $"{Code(file)} is loaded {Count(copies)} times",
            $"{Count(copies)} copies in separate load contexts" + (bytes > 0 ? $" · {Bytes(bytes)} of images" : "")
                + (others > 0 ? $" · {Plural(others, "other assembly", "other assemblies")} loaded {MinCopies} times or more" : ""),
            "Every `AssemblyLoadContext` (and, on .NET Framework, every AppDomain) gets its own copy of each assembly loaded into it: its own types, statics and compiled code. A context is only unloaded if it was created collectible, `Unload()` was called, and nothing outside still references anything from it: an instance, a `Type`, a delegate, an event subscription, a running thread. When any of those is missing, each reload of a plugin, script or tenant adds a copy that stays for good.",
            [
                "Create the context with `isCollectible: true` and call `Unload()` when its plugin is replaced.",
                $"Find what still references the old copies: search the heap for types from {Code(name)}. Each copy shows up as its own type, and the root path of an instance names the holder.",
                "If plugins never need reloading, load the assembly once and reuse it.",
            ],
            [Symptom.HighMemory],
            [EntityUri.TypeRef("System.Runtime.Loader.AssemblyLoadContext", "AssemblyLoadContext", "one per loaded copy")],
            Scores.For(severity, Confidence.High, Math.Min(1, copies / 500.0))),
            VerdictHeadline: $"{file} is loaded {Count(copies)} times.",
            VerdictBody: "Each copy sits in its own load context with its own types, statics and compiled code, and none of them has been unloaded. Every reload adds another.");
    }

    private static List<string> TypeNames(ClrRuntime runtime, ClrModule module)
    {
        var names = new List<string>();
        try
        {
            foreach (var (methodTable, _) in module.EnumerateTypeDefToMethodTableMap())
            {
                if (runtime.Heap.GetTypeByMethodTable(methodTable)?.Name is { Length: > 0 } name && name != "<Module>")
                {
                    names.Add(name);
                    if (names.Count >= 8)
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
            // A module whose type map can't be read still counts; it just isn't described.
        }

        return names;
    }
}
