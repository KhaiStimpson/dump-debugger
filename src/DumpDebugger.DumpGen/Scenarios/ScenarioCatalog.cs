namespace Contoso.Scenarios;

/// <param name="Crashes">The scenario ends the process itself, so the runtime writes the dump as it dies (the driver doesn't).</param>
/// <param name="NeedsSqlServer">The driver hosts a <see cref="FakeSqlServer"/> and passes its port in <see cref="FakeSqlServer.PortVariable"/>, so the database stays out of the victim's dump.</param>
public sealed record Scenario(string Name, string Description, Func<Task> RunAsync, bool Crashes = false, bool NeedsSqlServer = false);

public static class ScenarioCatalog
{
    public static readonly IReadOnlyList<Scenario> All =
    [
        new("simple", "Idle console process with a few thousand strings on the heap.", SimpleScenario.RunAsync),
        new("deadlock", "ASP.NET Core app: checkout and inventory requests deadlock on two locks; more requests queue behind them.", DeadlockScenario.RunAsync),
        new("leak", "Console app: rendered PDFs cached forever in a static ReportCache, and 120k cart snapshots in a static list that only grows.", LeakScenario.RunCurrentAsync),
        new("leak-baseline", "The same app hours earlier: two cached reports and 5k cart snapshots. Compare it with leak.", LeakScenario.RunBaselineAsync),
        new("bigheap", "Performance fixture: ~2 GB heap of millions of small objects (for heap-walk and root-path timing).", BigHeapScenario.RunAsync),
        new("threadpool", "ASP.NET Core app: all 24 pool workers blocked on a gate that never opens; requests queue behind them.", ThreadPoolScenario.RunAsync),
        new("exceptions", "ASP.NET Core app: payment requests stuck in a retry back-off inside catch blocks, plus 2,000 exceptions kept by an error log.", ExceptionsScenario.RunAsync),
        new("finalizer", "Console app: the finalizer thread is blocked on a lock, and 50,000 finalizable objects wait behind it.", FinalizerScenario.RunAsync),
        new("loh", "Console app: 1 MB buffers kept and dropped alternately, leaving the Large Object Heap half free space.", LohScenario.RunAsync),
        new("httpclient", "ASP.NET Core app: a new HttpClient per request; 200 in-flight calls each own a handler and a socket.", HttpClientScenario.RunAsync),
        new("timers", "Console app: a timer per session in a dictionary that is never cleaned (5,000 timers).", TimersScenario.RunAsync),
        new("crash", "Console app: an unhandled exception on a background thread crashes the process; the runtime writes the dump.", CrashScenario.RunAsync, Crashes: true),
        new("strings", "Console app: 300,000 product rows, each with its own copy of a description and category that repeat across rows.", DuplicateStringsScenario.RunAsync),
        new("events", "Console app: 5,000 price tickers subscribe to a static event and never unsubscribe.", EventLeakScenario.RunAsync),
        new("pinned", "Console app: 4,000 frame buffers pinned with GCHandle.Alloc and never freed, scattered through the heap.", PinnedScenario.RunAsync),
        new("threads", "Console app: a dedicated thread per connected device, 300 of them parked waiting for a message.", ThreadPerDeviceScenario.RunAsync),
        new("syncoverasync", "ASP.NET Core app: 200 pricing requests block pool threads on .Result of a downstream call that never answers.", SyncOverAsyncScenario.RunAsync),
        new("asynclock", "Console app: a reservation awaits a SemaphoreSlim it already holds, 40 more queue behind it, and 12 label requests await a TaskCompletionSource nothing can complete.", AsyncLockScenario.RunAsync),
        new("hotcpu", "Console app: four import threads stuck in a backtracking regular expression, and a rollup loop that never exits.", HotCpuScenario.RunAsync),
        new("stackoverflow", "Console app: unbounded recursion over cyclic data overflows the stack; the runtime writes the dump.", StackOverflowScenario.RunAsync, Crashes: true),
        new("failfast", "Console app: an invariant check calls Environment.FailFast; the runtime writes the dump.", FailFastScenario.RunAsync, Crashes: true),
        new("connpool", "ASP.NET Core app: all 20 SQL connections are taken (14 held by exports awaiting a stuck PDF renderer, 6 leaked on an early return), and 30 customer requests block in SqlConnection.Open.", ConnectionPoolScenario.RunAsync, NeedsSqlServer: true),
        new("assemblies", "Console app: 300 XmlSerializer assemblies generated one per call, and a plugin loaded into 60 load contexts that never unload.", AssembliesScenario.RunAsync),
    ];

    public static Scenario Get(string name) =>
        All.FirstOrDefault(s => s.Name == name)
        ?? throw new ArgumentException($"Unknown scenario '{name}'. Known: {string.Join(", ", All.Select(s => s.Name))}");

    /// <summary>Tells the driver the process is in the state worth dumping, then parks forever.</summary>
    public static async Task ReadyAndParkAsync()
    {
        Console.WriteLine("READY");
        Console.Out.Flush();
        await Task.Delay(Timeout.Infinite);
    }
}
