using System.Diagnostics;
using DumpDebugger.Core.Dump;

namespace DumpDebugger_App.Services;

/// <summary>
/// Finds and launches the architecture-matching DumpDebugger.Worker process (PLAN.md §2.2).
/// Search order: an env var override (dev convenience), then "Workers\win-{rid}\" next to
/// the App executable (the shape a real package would ship).
/// </summary>
public static class WorkerLauncher
{
    public static Process Launch(DumpArchitecture architecture, string pipeName)
    {
        var exePath = FindWorkerExecutable(architecture);
        var psi = new ProcessStartInfo(exePath, pipeName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start worker: {exePath}");
    }

    private static string FindWorkerExecutable(DumpArchitecture architecture)
    {
        var rid = architecture switch
        {
            DumpArchitecture.X86 => "win-x86",
            DumpArchitecture.X64 => "win-x64",
            _ => throw new NotSupportedException(
                $"No worker available for dump architecture '{architecture}' (v1 supports x86/x64 only, PLAN.md §11.2)."),
        };

        var envOverride = Environment.GetEnvironmentVariable($"DUMPDEBUGGER_WORKER_{rid.ToUpperInvariant().Replace('-', '_')}");
        if (envOverride is not null && File.Exists(envOverride))
        {
            return envOverride;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, "Workers", rid, "DumpDebugger.Worker.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException(
            $"Worker executable for {rid} not found at '{candidate}' and no " +
            $"DUMPDEBUGGER_WORKER_{rid.ToUpperInvariant().Replace('-', '_')} override was set.");
    }
}
