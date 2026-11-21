using System.Reflection.Metadata;
using DumpDebugger.Analysis.Frames;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Session;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Tests;

/// <summary>
/// Where a module's PDB is looked for. These drive the search with a real portable PDB (this
/// test run's own DumpDebugger.Core.pdb) standing in for the app's, and a module path that
/// doesn't exist, the way a production dump looks on a developer's machine.
/// </summary>
public sealed class PdbSearchTests : IDisposable
{
    private const string GoneModule = @"X:\deploy\app\Contoso.Orders.dll";
    private const string GonePdb = @"X:\build\obj\Release\Contoso.Orders.pdb";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dd-symbols-" + Guid.NewGuid().ToString("N"));
    private readonly string _realPdb = Path.Combine(AppContext.BaseDirectory, "DumpDebugger.Core.pdb");
    private readonly Guid _id;

    public PdbSearchTests()
    {
        Directory.CreateDirectory(_root);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(_realPdb));
        _id = new BlobContentId(provider.GetMetadataReader().DebugMetadataHeader!.Id).Guid;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SymbolModule Probe(Guid id, params string[] symbolPaths)
    {
        using var locator = new SourceLocator();
        locator.Configure(symbolPaths, []);
        return locator.Probe(GoneModule, new PdbInfo(GonePdb, id, 1));
    }

    private string Place(params string[] relative)
    {
        var path = Path.Combine([_root, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(_realPdb, path);
        return path;
    }

    [Fact]
    public void With_no_symbol_folder_a_pdb_that_is_not_beside_the_module_is_not_found()
    {
        var result = Probe(_id);
        Assert.Equal(SymbolState.NotFound, result.State);
        Assert.Contains("Contoso.Orders.pdb", result.Detail);
        Assert.Contains("Add the folder", result.Detail);
    }

    [Fact]
    public void A_pdb_directly_in_a_symbol_folder_is_used()
    {
        var pdb = Place("Contoso.Orders.pdb");
        var result = Probe(_id, _root);
        Assert.Equal(SymbolState.Loaded, result.State);
        Assert.Equal(pdb, result.PdbPath);
    }

    [Fact]
    public void A_pdb_in_a_subfolder_is_found_so_a_build_output_or_checkout_root_can_be_added_as_is()
    {
        var pdb = Place("src", "Orders", "bin", "Release", "net8.0", "Contoso.Orders.pdb");
        var result = Probe(_id, _root);
        Assert.Equal(SymbolState.Loaded, result.State);
        Assert.Equal(pdb, result.PdbPath);
    }

    [Fact]
    public void A_symbol_store_layout_is_understood()
    {
        var pdb = Place("Contoso.Orders.pdb", $"{_id:N}FFFFFFFF".ToUpperInvariant(), "Contoso.Orders.pdb");
        var result = Probe(_id, _root);
        Assert.Equal(SymbolState.Loaded, result.State);
        Assert.Equal(pdb, result.PdbPath);
    }

    [Fact]
    public void A_pdb_from_another_build_is_rejected_rather_than_giving_wrong_lines()
    {
        Place("Contoso.Orders.pdb");
        var result = Probe(Guid.NewGuid(), _root);
        Assert.Equal(SymbolState.Mismatched, result.State);
        Assert.Contains("different build", result.Detail);
    }

    [Fact]
    public void The_matching_build_wins_when_several_builds_are_on_disk()
    {
        // Same file name, two builds: one is some other assembly's PDB (a different id), one matches.
        var other = Path.Combine(_root, "old", "Contoso.Orders.pdb");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "DumpDebugger.Analysis.pdb"), other);
        var match = Place("new", "Contoso.Orders.pdb");

        var result = Probe(_id, _root);
        Assert.Equal(SymbolState.Loaded, result.State);
        Assert.Equal(match, result.PdbPath);
    }

    [Fact]
    public void A_windows_pdb_is_reported_as_unsupported_with_what_to_change()
    {
        File.WriteAllText(Path.Combine(_root, "Contoso.Orders.pdb"), "Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0");
        var result = Probe(_id, _root);
        Assert.Equal(SymbolState.Unsupported, result.State);
        Assert.Contains("portable", result.Detail);
    }

    [Fact]
    public void Settings_keep_absolute_folders_once_each_and_drop_the_rest()
    {
        var file = Path.Combine(_root, "symbols.json");
        var store = new SymbolSettingsStore(file);
        Assert.Empty(store.Load().SymbolPaths);

        var saved = store.Save(new SymbolSettings([@" ""C:\Symbols\"" ", @"c:\symbols", "relative\\path", ""], [@"D:\src\shop"]));
        Assert.Equal([@"C:\Symbols"], saved.SymbolPaths);
        Assert.Equal([@"D:\src\shop"], saved.SourcePaths);
        Assert.Equal(saved.SymbolPaths, new SymbolSettingsStore(file).Load().SymbolPaths);
    }
}

/// <summary>File, line and source text on real frames, end to end through the bridge.</summary>
[Collection(WorkspaceCollection.Name)]
public sealed class SourceLineTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dd-source-" + Guid.NewGuid().ToString("N"));
    private string _dump = "";

    public async Task InitializeAsync()
    {
        // Captured now, from the current DumpGen build, so its PDB and source are the matching ones.
        _dump = Path.Combine(_root, "threads.dmp");
        await FixtureDumps.GenerateAsync("threads", _dump);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Frames_carry_file_and_line_and_the_source_around_them_and_survive_a_settings_change()
    {
        var settingsFile = Path.Combine(_root, "symbols.json");
        await using var bridge = new BridgeHarness(new HostRpcServerOptions(SymbolSettingsFile: settingsFile));
        await bridge.CallAsync<DumpOpened>("dump.open", new { path = _dump });

        var modules = await bridge.CallAsync<List<SymbolModule>>("symbols.modules");
        var dumpGen = Assert.Single(modules, m => m.Module == "DumpDebugger.DumpGen.dll");
        Assert.Equal(SymbolState.Loaded, dumpGen.State);

        var frame = await ListenFrameAsync(bridge);
        Assert.Equal("AnalyzerDomain.cs", frame.SourceFile);
        Assert.NotNull(frame.SourcePath);

        var snippet = await bridge.CallAsync<SourceSnippet>("source.snippet", new { path = frame.SourcePath, line = frame.Line, context = 3 });
        Assert.Null(snippet.Unavailable);
        Assert.Equal(SourceOrigin.Disk, snippet.Origin);
        Assert.False(snippet.Modified);
        Assert.Contains("_messages.Wait()", Assert.Single(snippet.Lines, l => l.Number == frame.Line).Text);
        Assert.Equal(7, snippet.Lines.Count);

        // Only files a frame points at can be read through this method.
        var other = await bridge.CallAsync<SourceSnippet>("source.snippet", new { path = settingsFile, line = 1, context = 3 });
        Assert.NotNull(other.Unavailable);
        Assert.Empty(other.Lines);

        // Changing the folders re-resolves the open dump's frames in place: no reopen, lines still there.
        bridge.Platform.NextPickedFolder = _root;
        var picked = await bridge.CallAsync<PickedFolder>("symbols.pickFolder");
        var saved = await bridge.CallAsync<SymbolSettings>("symbols.set", new { symbolPaths = new[] { picked.Path }, sourcePaths = Array.Empty<string>() });
        Assert.Equal([_root], saved.SymbolPaths);
        Assert.Equal([_root], (await bridge.CallAsync<SymbolSettings>("symbols.get")).SymbolPaths);
        Assert.Equal(frame, await ListenFrameAsync(bridge));
    }

    private static async Task<StackFrame> ListenFrameAsync(BridgeHarness bridge)
    {
        var groups = await bridge.CallAsync<ThreadGroupsResult>("threads.groups", new { groupBy = "stack", state = "all" });
        var listeners = Assert.Single(groups.Groups, g => g.Where.StartsWith("DeviceListener.Listen", StringComparison.Ordinal));
        var thread = await bridge.CallAsync<ThreadDetail>("threads.get", new { id = listeners.ThreadIds[0] });
        return Assert.Single(thread.Frames, f => f.Display.StartsWith("DeviceListener.Listen", StringComparison.Ordinal));
    }
}
