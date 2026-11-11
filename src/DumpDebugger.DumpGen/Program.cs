using System.Diagnostics;
using Contoso.Scenarios;
using Microsoft.Diagnostics.NETCore.Client;

// Fixture generator (PLAN.md §9). Usage:
//   DumpDebugger.DumpGen <output.dmp> [--scenario <name>]
//   DumpDebugger.DumpGen --list
// The scenario runs in a child "victim" process; when it prints READY the driver writes a full
// dump of it and kills it.
if (args is ["--list"])
{
    foreach (var s in ScenarioCatalog.All)
    {
        Console.WriteLine($"{s.Name,-16} {s.Description}");
    }

    return 0;
}

if (args is ["--victim", var victimScenario, ..])
{
    await ScenarioCatalog.Get(victimScenario).RunAsync();
    return 0;
}

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: DumpDebugger.DumpGen <output.dmp> [--scenario <name>] | --list");
    return 1;
}

var outputPath = Path.GetFullPath(args[0]);
var scenario = args.Length >= 3 && args[1] == "--scenario" ? args[2] : "simple";
_ = ScenarioCatalog.Get(scenario); // validate before launching anything

var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--victim \"{scenario}\"")
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
};

// Crash scenarios are dumped by the runtime itself as the process dies (createdump), the way
// a real crash dump is captured, rather than by us while it's alive.
var crash = scenario == "crash";
if (crash)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.Delete(outputPath);
    psi.Environment["DOTNET_DbgEnableMiniDump"] = "1";
    psi.Environment["DOTNET_DbgMiniDumpType"] = "4";
    psi.Environment["DOTNET_DbgMiniDumpName"] = outputPath;
}

using var victim = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start victim process.");
if (crash)
{
    await victim.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
    for (var i = 0; i < 100 && !File.Exists(outputPath); i++)
    {
        await Task.Delay(100);
    }

    Console.WriteLine(File.Exists(outputPath) ? $"Wrote dump: {outputPath}" : "The runtime did not write a crash dump.");
    return File.Exists(outputPath) ? 0 : 1;
}

var deadline = Task.Delay(TimeSpan.FromSeconds(90));
string? line;
while (true)
{
    var read = victim.StandardOutput.ReadLineAsync();
    if (await Task.WhenAny(read, deadline) == deadline)
    {
        Console.Error.WriteLine("Victim process did not signal readiness within 90 s.");
        victim.Kill(entireProcessTree: true);
        return 1;
    }

    line = await read;
    if (line is null or "READY")
    {
        break;
    }

    Console.WriteLine($"  victim: {line}");
}

if (line != "READY")
{
    Console.Error.WriteLine("Victim process exited before signalling readiness.");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
new DiagnosticsClient(victim.Id).WriteDump(DumpType.Full, outputPath);
victim.Kill(entireProcessTree: true);
Console.WriteLine($"Wrote dump: {outputPath}");
return 0;
