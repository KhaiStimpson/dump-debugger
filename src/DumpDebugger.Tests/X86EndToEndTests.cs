using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;
using DumpDebugger.Core.Rpc;
using DumpDebugger.Session;

namespace DumpDebugger.Tests;

/// <summary>
/// Opt-in: set DUMPDEBUGGER_X86_DUMP to an x86 .NET dump (e.g. `DumpDebugger.DumpGen` built with
/// -r win-x86, scenario "simple"). Optionally DUMPDEBUGGER_WORKER_WIN_X86 selects which built x86
/// worker runs it (say, a published bundle's). No fixture is checked in: x86 dumps are large.
/// </summary>
public sealed class X86DumpFactAttribute : FactAttribute
{
    public X86DumpFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DUMPDEBUGGER_X86_DUMP") is not { } path || !File.Exists(path))
        {
            Skip = "Needs an x86 dump: set DUMPDEBUGGER_X86_DUMP.";
        }
    }
}

[Collection(WorkspaceCollection.Name)]
public class X86EndToEndTests
{
    [X86DumpFact]
    public async Task An_x86_dump_opens_in_a_large_address_aware_worker_and_finishes_analysis_offline()
    {
        var dump = Environment.GetEnvironmentVariable("DUMPDEBUGGER_X86_DUMP")!;
        using var appData = new TempDirectory();
        var previousRoot = AppPaths.DataRoot;
        AppPaths.DataRoot = appData.Path; // recents for this probe stay out of the user's profile
        try
        {
            await using var bridge = new BridgeHarness();
            var opened = await bridge.CallAsync<DumpOpened>("dump.open", new { path = dump }, TimeSpan.FromMinutes(5));

            Assert.Equal(DumpArchitecture.X86, opened.Metadata.Architecture);
            Assert.Null(opened.AnalysisError);
            var primary = opened.Metadata.PrimaryRuntime()!;
            Assert.True(primary.IsPrimary);
            Assert.True(primary.IsManagedAnalysisAvailable, primary.DacError);
            Assert.True(File.Exists(primary.DacPath), primary.DacPath);
            Assert.NotEqual(DacResolutionTier.None, primary.DacTier);
            Assert.Equal(primary.ManagedThreadCount, opened.Metadata.ThreadCount);

            var worker = bridge.Notifications.Select(n => n.Method == "worker.status" ? ContractJson.FromElement<WorkerStatus>(n.Params) : null)
                .Last(s => s is not null)!;
            Assert.Equal("X86", worker.Architecture);
            using (var process = System.Diagnostics.Process.GetProcessById(worker.ProcessId!.Value))
            using (var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(process.MainModule!.FileName)))
            {
                if (Environment.GetEnvironmentVariable("DUMPDEBUGGER_WORKER_WIN_X86") is { } chosen)
                {
                    Assert.Equal(Path.GetFullPath(chosen), process.MainModule.FileName, ignoreCase: true);
                }

                Assert.True(pe.PEHeaders.CoffHeader.Characteristics.HasFlag(System.Reflection.PortableExecutable.Characteristics.LargeAddressAware));
            }

            // Analysis must reach a terminal state: complete, or a reported failure, never a silent stall.
            var deadline = DateTime.UtcNow.AddMinutes(5);
            AnalysisState state;
            do
            {
                await Task.Delay(250);
                state = await bridge.CallAsync<AnalysisState>("analysis.get");
                Assert.DoesNotContain(bridge.Notifications, n => n.Method == "analysis.failed");
            }
            while (state.Diagnosis is not { Complete: true } && DateTime.UtcNow < deadline);

            var diagnosis = Assert.IsType<Diagnosis>(state.Diagnosis);
            Assert.True(diagnosis.Complete);
            Assert.Equal(primary.ManagedThreadCount, diagnosis.Dump.ThreadCount);
            Assert.Contains(primary.ProductVersion, diagnosis.Dump.Runtime);
            Assert.NotNull(Workspace.For(dump).TryLoadDiagnosis(opened.Metadata.RuntimeIdentity()));
        }
        finally
        {
            AppPaths.DataRoot = previousRoot;
        }
    }
}
