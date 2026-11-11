using System.Diagnostics;
using DumpDebugger.Core.Dump;

namespace DumpDebugger.Session;

/// <summary>
/// Finds and launches the architecture-matching DumpDebugger.Worker (PLAN.md §2.2). Search
/// order: a <c>DUMPDEBUGGER_WORKER_WIN_X64</c>/<c>_X86</c> override, then
/// <c>Workers\win-{arch}\</c> next to the host (the shipped layout), then the worker's own
/// build output in a source checkout (so tests and <c>dotnet run</c> work without a copy step).
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

    public static string RidFor(DumpArchitecture architecture) => architecture switch
    {
        DumpArchitecture.X86 => "win-x86",
        DumpArchitecture.X64 => "win-x64",
        _ => throw new NotSupportedException(
            $"No worker for dump architecture '{architecture}'. x86 and x64 dumps are supported."),
    };

    public static string FindWorkerExecutable(DumpArchitecture architecture)
    {
        var rid = RidFor(architecture);
        var envName = $"DUMPDEBUGGER_WORKER_{rid.ToUpperInvariant().Replace('-', '_')}";
        var envOverride = Environment.GetEnvironmentVariable(envName);
        if (envOverride is not null && File.Exists(envOverride))
        {
            return envOverride;
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Workers", rid, "DumpDebugger.Worker.exe"),
        };

        var repoSrc = FindAncestorWith(AppContext.BaseDirectory, "DumpDebugger.Worker");
        if (repoSrc is not null)
        {
            foreach (var config in new[] { "Debug", "Release" })
            {
                candidates.Add(Path.Combine(repoSrc, "DumpDebugger.Worker", "bin", config, "net10.0", rid, "DumpDebugger.Worker.exe"));
            }
        }

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"Worker for {rid} not found. Looked in: {string.Join("; ", candidates)}. " +
                $"Build it with 'dotnet build src/DumpDebugger.Worker -r {rid}' or set {envName}.");
    }

    private static string? FindAncestorWith(string start, string childDirectory)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, childDirectory)))
            {
                return dir.FullName;
            }

            var src = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(src, childDirectory)))
            {
                return src;
            }
        }

        return null;
    }
}
