using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;

namespace DumpDebugger.Core.Rpc;

public enum RpcTarget
{
    /// <summary>Handled by DumpDebugger.Host itself (dialogs, workspace, recents, compare).</summary>
    Host,

    /// <summary>Forwarded verbatim to the architecture-matched worker that has the dump open.</summary>
    Worker,
}

public sealed record RpcMethodInfo(string Name, Type Params, Type Result, RpcTarget Target);

public sealed record RpcNotificationInfo(string Name, Type Payload);

/// <summary>
/// Every method and notification that crosses the WebView2 bridge, with its params/result
/// contract. This is the single source of truth: the host dispatcher routes by
/// <see cref="RpcTarget"/>, and DumpDebugger.TsGen turns it into the typed
/// <c>RpcMethods</c>/<c>RpcNotifications</c> maps in <c>web/src/contracts.gen.ts</c>.
/// Worker methods must aggregate and paginate: nothing here returns a raw heap.
/// </summary>
public static class RpcCatalog
{
    public static readonly IReadOnlyList<RpcMethodInfo> Methods =
    [
        Host<NoParams, AppInfo>("app.info"),
        Host<TitlebarRegions, Ack>("window.setTitlebar"),
        Host<NoParams, PickedFile>("dump.pick"),
        Host<OpenDumpParams, DumpOpened>("dump.open"),
        Host<NoParams, Ack>("dump.close"),
        Host<NoParams, AnalysisState>("analysis.get"),
        Host<NoParams, Ack>("analysis.cancel"),
        Host<NoParams, IReadOnlyList<RecentDump>>("recents.list"),
        Host<PathParams, Ack>("recents.remove"),
        Host<NoParams, IReadOnlyList<SampleScenario>>("samples.list"),
        Host<GenerateSampleParams, GeneratedSample>("samples.generate"),
        Host<NoParams, IReadOnlyList<Pin>>("pins.list"),
        Host<PinUpsert, IReadOnlyList<Pin>>("pins.upsert"),
        Host<UriParams, IReadOnlyList<Pin>>("pins.remove"),
        Host<SaveReportParams, SavedReport>("report.save"),
        Host<NoParams, IReadOnlyList<CompareCandidate>>("compare.candidates"),
        Host<CompareParams, CompareResult>("compare.run"),

        Worker<AnalysisRunParams, Diagnosis>("analysis.run"),
        Worker<ThreadGroupsQuery, ThreadGroupsResult>("threads.groups"),
        Worker<ThreadQuery, ThreadDetail>("threads.get"),
        Worker<WaitGraphQuery, WaitGraph>("locks.graph"),
        Worker<NoParams, HeapSummary>("heap.summary"),
        Worker<HeapTypesQuery, Page<TypeRow>>("heap.types"),
        Worker<InstancesQuery, Page<InstanceRow>>("heap.instances"),
        Worker<NoParams, IReadOnlyList<LargeObject>>("heap.largeObjects"),
        Worker<ObjectQuery, ObjectView>("objects.inspect"),
        Worker<ReferrersQuery, Page<Referrer>>("objects.referrers"),
        Worker<RootsQuery, RootPathsResult>("objects.roots"),
        Worker<NoParams, AsyncStacksResult>("async.stacks"),
        Worker<SnapshotParams, HeapSnapshot>("snapshot.build"),
    ];

    public static readonly IReadOnlyList<RpcNotificationInfo> Notifications =
    [
        new("dump.progress", typeof(ProgressNotification)),
        new("worker.status", typeof(WorkerStatus)),
        new("analysis.updated", typeof(Diagnosis)),
        new("snapshot.ready", typeof(PathParams)),
    ];

    /// <summary>Contract types the web app uses directly that no method signature reaches.</summary>
    public static readonly IReadOnlyList<Type> ExtraExportedTypes = [typeof(EntityKind)];

    private static readonly Dictionary<string, RpcMethodInfo> ByName =
        Methods.ToDictionary(m => m.Name, StringComparer.Ordinal);

    public static RpcMethodInfo? Find(string name) => ByName.GetValueOrDefault(name);

    private static RpcMethodInfo Host<TParams, TResult>(string name) =>
        new(name, typeof(TParams), typeof(TResult), RpcTarget.Host);

    private static RpcMethodInfo Worker<TParams, TResult>(string name) =>
        new(name, typeof(TParams), typeof(TResult), RpcTarget.Worker);
}
