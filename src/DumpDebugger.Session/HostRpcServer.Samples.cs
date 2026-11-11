using System.Diagnostics;
using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Session;

public sealed partial class HostRpcServer
{
    /// <summary>
    /// "Generate a sample" on Home: runs the bundled DumpGen (<c>Tools\DumpGen</c> next to the
    /// host, or the repo build in a source checkout) to capture a real dump of a known problem
    /// into %LOCALAPPDATA%\DumpDebugger\Samples, then the page opens it like any other dump.
    /// </summary>
    private async Task<GeneratedSample> GenerateSampleAsync(string scenario, CancellationToken ct)
    {
        var exe = SampleGenerator.FindDumpGen()
                  ?? throw new Core.Rpc.RpcException(new Core.Rpc.RpcError(Core.Rpc.RpcError.InternalError, "The sample generator (DumpGen) isn't bundled with this build."));
        var output = Path.Combine(AppPaths.Ensure("Samples"), $"sample-{scenario}.dmp");
        await NotifyWebAsync("dump.progress", new ProgressNotification($"Generating '{scenario}' sample", 0.1, "Starting a sample app and waiting for it to reach the problem state")).ConfigureAwait(false);

        var psi = new ProcessStartInfo(exe, $"\"{output}\" --scenario {scenario}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(output))
        {
            throw new Core.Rpc.RpcException(new Core.Rpc.RpcError(Core.Rpc.RpcError.InternalError, $"Sample generation failed: {await stderr}"));
        }

        return new GeneratedSample(output);
    }
}

public static class SampleGenerator
{
    /// <summary>Scenarios offered on Home, in order. Mirrors DumpGen's catalog.</summary>
    public static readonly IReadOnlyList<SampleScenario> Scenarios =
    [
        new("deadlock", "Deadlock", "Two requests take two locks in opposite order; more requests queue behind them."),
    ];

    public static string? FindDumpGen()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "DumpGen", "DumpDebugger.DumpGen.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(dir.FullName, "src", "DumpDebugger.DumpGen", "bin", config, "net10.0", "DumpDebugger.DumpGen.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
