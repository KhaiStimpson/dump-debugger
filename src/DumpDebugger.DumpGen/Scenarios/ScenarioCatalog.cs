namespace Contoso.Scenarios;

public sealed record Scenario(string Name, string Description, Func<Task> RunAsync);

public static class ScenarioCatalog
{
    public static readonly IReadOnlyList<Scenario> All =
    [
        new("simple", "Idle console process with a few thousand strings on the heap.", SimpleScenario.RunAsync),
        new("deadlock", "ASP.NET Core app: checkout and inventory requests deadlock on two locks; more requests queue behind them.", DeadlockScenario.RunAsync),
        new("leak", "Console app: rendered PDFs cached forever in a static ReportCache, and 120k cart snapshots in a static list that only grows.", LeakScenario.RunCurrentAsync),
        new("leak-baseline", "The same app hours earlier: two cached reports and 5k cart snapshots. Compare it with leak.", LeakScenario.RunBaselineAsync),
        new("bigheap", "Performance fixture: ~2 GB heap of millions of small objects (for heap-walk and root-path timing).", BigHeapScenario.RunAsync),
        new("syncoverasync", "ASP.NET Core app: 200 pricing requests block pool threads on .Result of a downstream call that never answers.", SyncOverAsyncScenario.RunAsync),
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
