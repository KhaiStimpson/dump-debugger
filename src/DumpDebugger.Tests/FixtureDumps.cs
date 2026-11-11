using System.Collections.Concurrent;
using System.Diagnostics;

namespace DumpDebugger.Tests;

/// <summary>
/// Real dumps produced by DumpDebugger.DumpGen. Dumps aren't committed (they're large and
/// machine-specific), so a missing fixture is generated on first use into fixtures/ and
/// reused afterwards. Delete fixtures/&lt;scenario&gt;.dmp to force a fresh capture.
/// </summary>
internal static class FixtureDumps
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Pending = new();

    public static Task<string> GetAsync(string scenario) =>
        Pending.GetOrAdd(scenario, s => new Lazy<Task<string>>(() => EnsureAsync(s))).Value;

    private static async Task<string> EnsureAsync(string scenario)
    {
        var path = Repo.Path("fixtures", $"{scenario}.dmp");
        if (File.Exists(path))
        {
            return path;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var psi = new ProcessStartInfo("dotnet", $"run --project \"{Repo.Path("src", "DumpDebugger.DumpGen")}\" -- \"{path}\" --scenario {scenario}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !File.Exists(path))
        {
            throw new InvalidOperationException($"DumpGen failed for '{scenario}': {await stdout}\n{await stderr}");
        }

        return path;
    }
}

/// <summary>Tests that open fixtures through the bridge write to (and some reset) the fixture's workspace sidecar.</summary>
[CollectionDefinition(Name)]
public sealed class WorkspaceCollection
{
    public const string Name = "fixture workspaces";
}
