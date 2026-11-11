namespace Contoso.Scenarios;

public sealed record Scenario(string Name, string Description, Func<Task> RunAsync);

public static class ScenarioCatalog
{
    public static readonly IReadOnlyList<Scenario> All =
    [
        new("simple", "Idle console process with a few thousand strings on the heap.", SimpleScenario.RunAsync),
        new("deadlock", "ASP.NET Core app: checkout and inventory requests deadlock on two locks; more requests queue behind them.", DeadlockScenario.RunAsync),
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
