using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

// Minimal fixture generator for Phase 0 validation (PLAN.md §9 describes the full fixture
// matrix; this is a stripped-down single-scenario version to prove the open/index/metadata
// path end to end). Usage: DumpDebugger.DumpGen <output.dmp>
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: DumpDebugger.DumpGen <output.dmp>");
    return 1;
}

var outputPath = Path.GetFullPath(args[0]);

if (args.Length >= 2 && args[1] == "--victim")
{
    // Running as the process to be dumped: allocate some heap state and idle.
    var data = new List<string>();
    for (var i = 0; i < 10_000; i++)
    {
        data.Add($"dump-debugger-fixture-object-{i}");
    }

    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

var selfPath = Process.GetCurrentProcess().MainModule!.FileName!;
var psi = new ProcessStartInfo(selfPath, $"\"{outputPath}\" --victim")
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
};

using var victim = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start victim process.");
var ready = await victim.StandardOutput.ReadLineAsync();
if (ready != "READY")
{
    Console.Error.WriteLine("Victim process did not signal readiness.");
    victim.Kill();
    return 1;
}

// Give the runtime a moment to settle after startup before snapshotting.
await Task.Delay(500);

var client = new DiagnosticsClient(victim.Id);
client.WriteDump(DumpType.Full, outputPath);

victim.Kill();
Console.WriteLine($"Wrote dump: {outputPath}");
return 0;
