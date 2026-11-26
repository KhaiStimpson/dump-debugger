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
        new("threadpool", "Thread-pool starvation", "Every pool worker is blocked on a gate; requests and background work queue up."),
        new("syncoverasync", "Sync-over-async", "200 requests block on .Result of a downstream call that never answers."),
        new("exceptions", "Exceptions", "Requests stuck in a retry back-off inside catch blocks, plus an exception storm."),
        new("finalizer", "Finalizer backlog", "The finalizer thread is blocked on a lock; 50,000 objects wait behind it."),
        new("leak", "Memory: cached reports", "Rendered PDFs cached forever in a static, plus a list of cart snapshots that only grows."),
        new("loh", "LOH fragmentation", "Large buffers kept and dropped alternately: half the Large Object Heap is free space."),
        new("httpclient", "HttpClient per call", "A new HttpClient for every request: hundreds of handlers and sockets."),
        new("timers", "Timer leak", "A timer per session in a dictionary nobody cleans."),
        new("strings", "Duplicate strings", "300,000 rows that each hold their own copy of the same few descriptions."),
        new("events", "Event-handler leak", "5,000 objects subscribed to a static event and never unsubscribed."),
        new("pinned", "Pinned buffers", "4,000 buffers pinned with GCHandle.Alloc and never freed."),
        new("threads", "Thread per device", "300 dedicated threads, each parked waiting for one device."),
        new("crash", "Crash", "An unhandled exception on a background thread; the runtime writes the dump as the process dies."),
        new("asynclock", "Async deadlock", "A reservation awaits a SemaphoreSlim it already holds; 40 more queue behind it and no thread is blocked."),
        new("hotcpu", "Runaway CPU", "Four threads stuck in a backtracking regular expression, and a loop that never exits."),
        new("stackoverflow", "Stack overflow", "Unbounded recursion over cyclic data; the runtime writes the dump as the process dies."),
        new("failfast", "FailFast", "An invariant check calls Environment.FailFast; the runtime writes the dump as the process dies."),
        new("connpool", "Connection pool exhausted", "All 20 SQL connections are taken: 14 held by exports awaiting a stuck renderer, 6 leaked; 30 requests wait in Open()."),
        new("assemblies", "Assembly leak", "An XmlSerializer assembly generated per call, and a plugin loaded into 60 load contexts."),
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
