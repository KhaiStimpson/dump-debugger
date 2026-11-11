namespace Contoso.Scenarios;

public static class SimpleScenario
{
    private static readonly List<string> Data = [];

    public static Task RunAsync()
    {
        for (var i = 0; i < 10_000; i++)
        {
            Data.Add($"dump-debugger-fixture-object-{i}");
        }

        return ScenarioCatalog.ReadyAndParkAsync();
    }
}
