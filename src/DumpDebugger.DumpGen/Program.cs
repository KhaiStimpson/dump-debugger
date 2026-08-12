using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

// Minimal fixture generator (PLAN.md §9 describes the full fixture matrix; this is a
// stripped-down subset covering the scenarios validated so far).
// Usage: DumpDebugger.DumpGen <output.dmp> [--scenario simple|deadlock]
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: DumpDebugger.DumpGen <output.dmp> [--scenario simple|deadlock]");
    return 1;
}

var outputPath = Path.GetFullPath(args[0]);
var scenario = args.Length >= 2 && args[0] == "--victim" ? args[1] : GetScenarioArg(args);

if (args.Length >= 1 && args[0] == "--victim")
{
    RunVictim(scenario);
    return 0;
}

var selfPath = Process.GetCurrentProcess().MainModule!.FileName!;
var psi = new ProcessStartInfo(selfPath, $"--victim \"{scenario}\"")
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

// Give the runtime a moment to settle (and, for "deadlock", let both threads actually block)
// before snapshotting.
await Task.Delay(1000);

var client = new DiagnosticsClient(victim.Id);
client.WriteDump(DumpType.Full, outputPath);

victim.Kill();
Console.WriteLine($"Wrote dump: {outputPath}");
return 0;

static string GetScenarioArg(string[] a) =>
    a.Length >= 3 && a[1] == "--scenario" ? a[2] : "simple";

static void RunVictim(string scenario)
{
    if (scenario == "deadlock")
    {
        RunDeadlockVictim();
    }
    else
    {
        RunSimpleVictim();
    }
}

static void RunSimpleVictim()
{
    var data = new List<string>();
    for (var i = 0; i < 10_000; i++)
    {
        data.Add($"dump-debugger-fixture-object-{i}");
    }

    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
}

static void RunDeadlockVictim()
{
    var lockA = new object();
    var lockB = new object();
    using var bothEntered = new Barrier(2);

    var t1 = new Thread(() =>
    {
        lock (lockA)
        {
            bothEntered.SignalAndWait();
            Thread.Sleep(200);
            lock (lockB)
            {
                // unreachable in the deadlock case
            }
        }
    }) { IsBackground = true, Name = "DeadlockThread1" };

    var t2 = new Thread(() =>
    {
        lock (lockB)
        {
            bothEntered.SignalAndWait();
            Thread.Sleep(200);
            lock (lockA)
            {
                // unreachable in the deadlock case
            }
        }
    }) { IsBackground = true, Name = "DeadlockThread2" };

    t1.Start();
    t2.Start();

    // Both threads hold their first lock and are racing to enter the other's; give them time
    // to actually deadlock before signalling readiness to snapshot.
    Thread.Sleep(1000);
    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
}
