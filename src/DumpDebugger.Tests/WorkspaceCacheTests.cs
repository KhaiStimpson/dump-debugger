using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Dump;
using DumpDebugger.Session;

namespace DumpDebugger.Tests;

public class WorkspaceCacheTests
{
    private static RuntimeInfo Runtime(RuntimeFamily family, string version, bool loaded, bool primary = false) =>
        new(family, version, version, loaded ? "C:\\dac.dll" : null, loaded ? DacResolutionTier.AppCache : DacResolutionTier.None,
            loaded, loaded ? null : "missing", loaded ? 1 : null, primary);

    private static DumpMetadata Metadata(params RuntimeInfo[] runtimes) =>
        new("w3wp.dmp", 1, DumpArchitecture.X86, true, false, null, "w3wp.exe", 1, runtimes, 0, 0);

    private static readonly string FrameworkOnly = Metadata(
        Runtime(RuntimeFamily.NetFramework, "4.8.9339.0", true, primary: true),
        Runtime(RuntimeFamily.NetCore, "10.0.1326.1", false)).RuntimeIdentity();

    private static readonly string BothLoaded = Metadata(
        Runtime(RuntimeFamily.NetCore, "10.0.1326.1", true, primary: true),
        Runtime(RuntimeFamily.NetFramework, "4.8.9339.0", true)).RuntimeIdentity();

    private static Diagnosis Diagnosis(string headline) => new(
        Core.Contracts.Diagnosis.CurrentSchemaVersion,
        new DumpSummary("w3wp.dmp", "w3wp.dmp", 1, ".NET 10.0.13", "x86", 168, "w3wp.exe", "local cache"),
        new Verdict(Severity.Ok, Confidence.High, headline, "", [], null),
        [], [], [], [], 1, Complete: true, FromCache: false, DateTimeOffset.UnixEpoch);

    private static HeapSnapshot Snapshot() => new(HeapSnapshot.CurrentSchemaVersion,
        Diagnosis("x").Dump, null, 0, 0, 0, 0, [], [], [], []);

    [Fact]
    public void Runtime_identity_changes_when_a_runtime_becomes_loadable_or_the_primary_changes()
    {
        Assert.NotEqual(FrameworkOnly, BothLoaded);
        var samePrimaryReordered = Metadata(
            Runtime(RuntimeFamily.NetFramework, "4.8.9339.0", true),
            Runtime(RuntimeFamily.NetCore, "10.0.1326.1", true, primary: true)).RuntimeIdentity();
        Assert.Equal(BothLoaded, samePrimaryReordered);
        var otherPrimary = Metadata(
            Runtime(RuntimeFamily.NetCore, "10.0.1326.1", true),
            Runtime(RuntimeFamily.NetFramework, "4.8.9339.0", true, primary: true)).RuntimeIdentity();
        Assert.NotEqual(BothLoaded, otherPrimary);
    }

    [Fact]
    public void Primary_runtime_is_the_flagged_one_even_when_not_first()
    {
        var md = Metadata(Runtime(RuntimeFamily.NetFramework, "4.8", true), Runtime(RuntimeFamily.NetCore, "10.0", true, primary: true));
        Assert.Equal(RuntimeFamily.NetCore, md.PrimaryRuntime()!.Family);
    }

    [Fact]
    public void A_diagnosis_from_other_runtimes_is_not_reused_and_pins_survive()
    {
        using var dir = new TempDirectory();
        var dump = Path.Combine(dir.Path, "w3wp.dmp");
        File.WriteAllText(dump, "not a real dump");
        var workspace = Workspace.For(dump);
        workspace.Write("pins.json", new[] { "thread://12" });

        workspace.SaveDiagnosis(Diagnosis("framework only"), FrameworkOnly);
        Assert.Equal("framework only", workspace.TryLoadDiagnosis(FrameworkOnly)!.Verdict.Headline);
        Assert.Null(workspace.TryLoadDiagnosis(BothLoaded)); // a DAC was downloaded since: re-analyze

        workspace.SaveDiagnosis(Diagnosis("both"), BothLoaded);
        Assert.Equal("both", workspace.TryLoadDiagnosis(BothLoaded)!.Verdict.Headline);
        Assert.Null(workspace.TryLoadDiagnosis(FrameworkOnly));
        Assert.Equal(["thread://12"], workspace.Read<string[]>("pins.json")!);
    }

    [Fact]
    public void A_pre_identity_workspace_manifest_is_treated_as_stale_not_deleted()
    {
        using var dir = new TempDirectory();
        var dump = Path.Combine(dir.Path, "w3wp.dmp");
        File.WriteAllText(dump, "not a real dump");
        var workspace = Workspace.For(dump);
        workspace.SaveDiagnosis(Diagnosis("old engine"), BothLoaded);
        var info = new FileInfo(dump);
        // The manifest an earlier build wrote: no runtime identity.
        File.WriteAllText(workspace.PathOf("workspace.json"),
            $$"""{"engineVersion":"{{Engine.Version}}","dumpSize":{{info.Length}},"dumpWriteTicks":{{info.LastWriteTimeUtc.Ticks}}}""");

        Assert.Null(workspace.TryLoadDiagnosis(BothLoaded));
        Assert.True(File.Exists(workspace.PathOf("diagnosis.json")));
    }

    [Fact]
    public void A_snapshot_is_tied_to_the_runtime_identity_it_was_built_from()
    {
        using var dir = new TempDirectory();
        var dump = Path.Combine(dir.Path, "w3wp.dmp");
        File.WriteAllText(dump, "not a real dump");
        var workspace = Workspace.For(dump);

        workspace.SaveSnapshot(Snapshot(), FrameworkOnly);
        Assert.NotNull(workspace.TryLoadSnapshot(FrameworkOnly));
        Assert.False(workspace.HasSnapshotFor(BothLoaded)); // re-analysis must rebuild it
        Assert.True(workspace.HasSnapshot); // a compare baseline only has to be self-consistent

        // A new diagnosis under another identity doesn't make the old snapshot look current.
        workspace.SaveDiagnosis(Diagnosis("both"), BothLoaded);
        Assert.False(workspace.HasSnapshotFor(BothLoaded));
        File.AppendAllText(dump, "changed");
        Assert.False(workspace.HasSnapshot);
    }

    [Fact]
    public void The_built_x86_worker_is_large_address_aware()
    {
        var exe = WorkerLauncher.FindWorkerExecutable(DumpArchitecture.X86);
        using var stream = File.OpenRead(exe);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        Assert.Equal(System.Reflection.PortableExecutable.Machine.I386, pe.PEHeaders.CoffHeader.Machine);
        Assert.True(pe.PEHeaders.CoffHeader.Characteristics.HasFlag(System.Reflection.PortableExecutable.Characteristics.LargeAddressAware),
            $"{exe}: characteristics {pe.PEHeaders.CoffHeader.Characteristics}");
    }
}
