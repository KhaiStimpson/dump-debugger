using System.Net;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using DumpDebugger.Analysis;
using DumpDebugger.Analysis.Dac;
using DumpDebugger.Core.Dump;

namespace DumpDebugger.Tests;

/// <summary>Temp directory deleted after the test: these tests never touch the real app DAC cache.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dd-test-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Stands in for the symbol server; records every request so tests can prove none were made.</summary>
internal sealed class FakeSymbolServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        return respond(request, ct);
    }

    public static FakeSymbolServer Serving(byte[] body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));
}

public class DacResolutionTests
{
    // Any intact x86 DLL works as a stand-in DAC: an AnyCPU managed assembly has an I386 COFF header.
    private static readonly string SampleDll = typeof(DacCache).Assembly.Location;
    private static readonly byte[] SampleBytes = File.ReadAllBytes(SampleDll);
    private static readonly (int Timestamp, int ImageSize) SampleIdentity = ReadIdentity(SampleDll);

    private static (int, int) ReadIdentity(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        return (pe.PEHeaders.CoffHeader.TimeDateStamp, pe.PEHeaders.PEHeader!.SizeOfImage);
    }

    private static string Place(string cache, byte[] bytes, string name = "mscordaccore.dll", (int, int)? key = null)
    {
        var (ts, size) = key ?? SampleIdentity;
        var path = DacCache.KeyPath(cache, name, ts, size);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Task<string?> Download(DacDownloader downloader, string cache, CancellationToken ct = default, List<long>? progress = null) =>
        downloader.DownloadAsync(cache, "mscordaccore.dll", SampleIdentity.Timestamp, SampleIdentity.ImageSize, true, Architecture.X86,
            (done, _) => progress?.Add(done), ct);

    [Fact]
    public void Cache_keys_match_the_symbol_server_layout_ClrMD_uses()
    {
        Assert.Equal(System.IO.Path.Combine("C:\\c", "mscordaccore.dll", "1234567810a000", "mscordaccore.dll"),
            DacCache.KeyPath("C:\\c", "MSCORDACCORE.dll", 0x12345678, 0x10a000));
    }

    [Fact]
    public void An_intact_cached_dac_is_found_offline()
    {
        using var cache = new TempDirectory();
        var path = Place(cache.Path, SampleBytes);
        Assert.Equal(path, DacCache.TryGet(cache.Path, "mscordaccore.dll", SampleIdentity.Timestamp, SampleIdentity.ImageSize, true, Architecture.X86));
    }

    [Fact]
    public void A_truncated_cached_dac_is_rejected_not_handed_to_ClrMD()
    {
        using var cache = new TempDirectory();
        Place(cache.Path, SampleBytes[..(SampleBytes.Length / 2)]);
        Assert.Null(DacCache.TryGet(cache.Path, "mscordaccore.dll", SampleIdentity.Timestamp, SampleIdentity.ImageSize, true, Architecture.X86));

        // ClrMD's own (unvalidated) lookups through the locator reject it too when it asks for validation.
        var options = DacResolver.BuildOptions(cache.Path);
        Assert.Null(options.FileLocator!.FindPEImage("mscordaccore.dll", SampleIdentity.Timestamp, SampleIdentity.ImageSize, true));
    }

    [Fact]
    public void A_cached_file_with_the_wrong_identity_or_architecture_is_rejected()
    {
        using var cache = new TempDirectory();
        Place(cache.Path, SampleBytes, key: (SampleIdentity.Timestamp + 1, SampleIdentity.ImageSize));
        Assert.Null(DacCache.TryGet(cache.Path, "mscordaccore.dll", SampleIdentity.Timestamp + 1, SampleIdentity.ImageSize, true, Architecture.X86));
        Place(cache.Path, SampleBytes);
        Assert.Null(DacCache.TryGet(cache.Path, "mscordaccore.dll", SampleIdentity.Timestamp, SampleIdentity.ImageSize, true, Architecture.X64));
    }

    [Fact]
    public void A_dac_archived_under_the_runtime_key_is_checked_structurally_only()
    {
        // Long-name Framework DACs live under clr.dll's timestamp/size, not their own.
        using var cache = new TempDirectory();
        var path = Place(cache.Path, SampleBytes, "mscordacwks_x86_x86_4.8.9339.00.dll", (0x11111111, 0x222000));
        Assert.Equal(path, DacCache.TryGet(cache.Path, "mscordacwks_x86_x86_4.8.9339.00.dll", 0x11111111, 0x222000, false, Architecture.X86));
    }

    [Fact]
    public void ClrMD_gets_an_offline_cache_only_locator()
    {
        using var cache = new TempDirectory();
        var options = DacResolver.BuildOptions(cache.Path);
        Assert.Empty(options.SymbolPaths);
        Assert.Equal("CachedFileLocator", options.FileLocator!.GetType().Name);
        Assert.Null(options.FileLocator.FindPEImage("mscordaccore.dll", 1, 2, false));
    }

    [Fact]
    public async Task A_download_is_validated_written_atomically_and_reported()
    {
        using var cache = new TempDirectory();
        var server = FakeSymbolServer.Serving(SampleBytes);
        using var downloader = new DacDownloader(new Uri("https://symbols.test/download/symbols"), TimeSpan.FromSeconds(10), server);
        var progress = new List<long>();

        var path = await Download(downloader, cache.Path, progress: progress);

        Assert.Equal(DacCache.KeyPath(cache.Path, "mscordaccore.dll", SampleIdentity.Timestamp, SampleIdentity.ImageSize), path);
        Assert.Equal(SampleBytes, File.ReadAllBytes(path!));
        Assert.Equal($"https://symbols.test/download/symbols/mscordaccore.dll/{(uint)SampleIdentity.Timestamp:x8}{SampleIdentity.ImageSize:x}/mscordaccore.dll",
            Assert.Single(server.Requests).AbsoluteUri);
        Assert.Equal(SampleBytes.Length, progress[^1]);
        Assert.Empty(Directory.GetFiles(cache.Path, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_damaged_cache_entry_is_replaced_by_a_good_download()
    {
        using var cache = new TempDirectory();
        var damaged = Place(cache.Path, SampleBytes[..1000]);
        using var downloader = new DacDownloader(new Uri("https://symbols.test/"), TimeSpan.FromSeconds(10), FakeSymbolServer.Serving(SampleBytes));

        Assert.Equal(damaged, await Download(downloader, cache.Path));
        Assert.Equal(damaged, DacCache.TryGet(cache.Path, "mscordaccore.dll", SampleIdentity.Timestamp, SampleIdentity.ImageSize, true, Architecture.X86));
    }

    [Fact]
    public async Task A_truncated_download_never_enters_the_cache()
    {
        using var cache = new TempDirectory();
        using var downloader = new DacDownloader(new Uri("https://symbols.test/"), TimeSpan.FromSeconds(10), FakeSymbolServer.Serving(SampleBytes[..(SampleBytes.Length - 512)]));

        await Assert.ThrowsAsync<InvalidDataException>(() => Download(downloader, cache.Path));
        Assert.Empty(Directory.GetFiles(cache.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Not_on_the_server_is_a_miss_not_an_error()
    {
        using var cache = new TempDirectory();
        using var downloader = new DacDownloader(new Uri("https://symbols.test/"), TimeSpan.FromSeconds(10),
            new FakeSymbolServer((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Null(await Download(downloader, cache.Path));
    }

    [Fact]
    public async Task A_stalled_server_times_out_and_later_candidates_fail_fast()
    {
        using var cache = new TempDirectory();
        var server = new FakeSymbolServer(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        using var downloader = new DacDownloader(new Uri("https://symbols.test/"), TimeSpan.FromMilliseconds(200), server);

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => Download(downloader, cache.Path));
        Assert.Contains("symbols.test", timeout.Message);
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(downloader, cache.Path));
        Assert.Single(server.Requests); // the second candidate didn't wait out another timeout
    }

    [Fact]
    public async Task User_cancellation_is_not_reported_as_a_timeout()
    {
        using var cache = new TempDirectory();
        using var cts = new CancellationTokenSource(100);
        using var downloader = new DacDownloader(new Uri("https://symbols.test/"), TimeSpan.FromSeconds(30), new FakeSymbolServer(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download(downloader, cache.Path, cts.Token));
    }

    [Fact]
    public async Task A_server_error_status_is_reported_without_giving_up_on_the_server()
    {
        using var cache = new TempDirectory();
        var server = new FakeSymbolServer((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var downloader = new DacDownloader(new Uri("https://symbols.test/"), TimeSpan.FromSeconds(10), server);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Download(downloader, cache.Path));
        Assert.Contains("503", error.Message);
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(downloader, cache.Path));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task A_fixture_loads_offline_with_its_real_dac_path_and_provenance_and_no_network()
    {
        using var cache = new TempDirectory();
        var dump = await FixtureDumps.GetAsync("deadlock");
        var events = new List<string>();
        using var loaded = DumpLoader.Load(dump, new DacLookup(cache.Path, null, (stage, detail) => events.Add($"{stage}: {detail}")));

        var runtime = Assert.Single(loaded.Metadata.Runtimes);
        Assert.True(runtime.IsManagedAnalysisAvailable, runtime.DacError);
        Assert.True(runtime.IsPrimary);
        Assert.Equal(runtime, loaded.Metadata.PrimaryRuntime());
        // The DAC actually loaded, not ClrMD's first candidate name: a real, existing, full path.
        Assert.True(System.IO.Path.IsPathFullyQualified(runtime.DacPath!), runtime.DacPath);
        Assert.True(File.Exists(runtime.DacPath));
        Assert.Equal(DacResolutionTier.LocalMachine, runtime.DacTier);
        Assert.Equal(loaded.Runtimes[0].Threads.Length, runtime.ManagedThreadCount);
        Assert.Equal(runtime.ManagedThreadCount, loaded.Metadata.ThreadCount);
        Assert.Contains(events, e => e.StartsWith("Primary runtime: NetCore", StringComparison.Ordinal));
        Assert.EndsWith("/primary", loaded.Metadata.RuntimeIdentity());
    }

    [Fact]
    public async Task A_cached_dac_is_reported_as_app_cache_with_its_cache_path()
    {
        // Seed an isolated cache with the fixture's own DAC under its symbol-server key, then
        // hide the local runtime folder: the DAC must come from the cache and say so.
        using var cache = new TempDirectory();
        var dump = await FixtureDumps.GetAsync("deadlock");
        string expected;
        using (var probe = DumpLoader.Load(dump, new DacLookup(cache.Path, null)))
        {
            var clr = probe.DataTarget.ClrVersions.Single();
            var keyed = clr.DebuggingLibraries.First(d => d.Kind == Microsoft.Diagnostics.Runtime.DebugLibraryKind.Dac
                                                          && System.IO.Path.GetFileName(d.FileName) == d.FileName && d.IndexTimeStamp != 0
                                                          && d.ArchivedUnder == Microsoft.Diagnostics.Runtime.SymbolProperties.Self);
            expected = DacCache.KeyPath(cache.Path, keyed.FileName, keyed.IndexTimeStamp, keyed.IndexFileSize);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(expected)!);
            File.Copy(probe.Metadata.Runtimes[0].DacPath!, expected);
        }

        using var target = Microsoft.Diagnostics.Runtime.DataTarget.LoadDump(dump, DacResolver.BuildOptions(cache.Path));
        var resolution = DacResolver.TryCreateRuntime(target.ClrVersions.Single(), new DacLookup(cache.Path, null),
            d => System.IO.Path.GetFileName(d.FileName) == d.FileName, default);
        Assert.Null(resolution.Error);
        Assert.Equal(DacResolutionTier.AppCache, resolution.Tier);
        Assert.Equal(expected, resolution.DacPath);
        Assert.NotEmpty(resolution.Runtime!.Threads);
        resolution.Runtime.Dispose();
    }

    [Fact]
    public async Task A_consented_download_loads_the_runtime_and_is_reused_offline_next_time()
    {
        using var cache = new TempDirectory();
        var dump = await FixtureDumps.GetAsync("deadlock");
        string localDac;
        using (var probe = DumpLoader.Load(dump, new DacLookup(cache.Path, null)))
        {
            localDac = probe.Metadata.Runtimes[0].DacPath!;
        }

        // The "symbol server" hands out the real DAC bytes; the local-folder candidate is excluded.
        var server = FakeSymbolServer.Serving(File.ReadAllBytes(localDac));
        bool CacheOrServerOnly(Microsoft.Diagnostics.Runtime.DebugLibraryInfo d) => System.IO.Path.GetFileName(d.FileName) == d.FileName;
        using var target = Microsoft.Diagnostics.Runtime.DataTarget.LoadDump(dump, DacResolver.BuildOptions(cache.Path));
        var clr = target.ClrVersions.Single();

        Assert.NotNull(DacResolver.TryCreateRuntime(clr, new DacLookup(cache.Path, null), CacheOrServerOnly, default).Error); // offline: nothing yet

        using var downloader = new DacDownloader(new Uri("https://symbols.test/"), TimeSpan.FromSeconds(10), server);
        var events = new List<string>();
        var online = DacResolver.TryCreateRuntime(clr, new DacLookup(cache.Path, downloader, (s, d) => events.Add(s)), CacheOrServerOnly, default);
        Assert.Null(online.Error);
        Assert.Equal(DacResolutionTier.SymbolServer, online.Tier);
        Assert.StartsWith(cache.Path, online.DacPath);
        Assert.Contains("Downloading DAC", events);
        online.Runtime!.Dispose();

        var requests = server.Requests.Count;
        var offline = DacResolver.TryCreateRuntime(clr, new DacLookup(cache.Path, null), CacheOrServerOnly, default);
        Assert.Null(offline.Error);
        Assert.Equal(DacResolutionTier.AppCache, offline.Tier);
        Assert.Equal(online.DacPath, offline.DacPath);
        Assert.Equal(requests, server.Requests.Count);
        offline.Runtime!.Dispose();
    }

    [Theory]
    [InlineData(new[] { "NetFramework:0", "NetCore:168" }, 1)] // the w3wp case: idle Framework first in the dump
    [InlineData(new[] { "NetCore:168", "NetFramework:0" }, 0)]
    [InlineData(new[] { "NetFramework:40", "NetCore:12" }, 0)] // most managed threads wins regardless of family
    [InlineData(new[] { "NetFramework:5", "NetCore:5" }, 1)]   // tie: CoreCLR
    [InlineData(new[] { "NetCore:5", "NetCore:5" }, 0)]        // tie, same family: first in the dump
    [InlineData(new[] { "NetCore:missing", "NetFramework:0" }, 1)] // unloaded runtimes never win
    [InlineData(new[] { "NetCore:missing" }, -1)]
    [InlineData(new string[0], -1)]
    public void The_primary_runtime_is_chosen_deliberately(string[] runtimes, int expected)
    {
        var input = runtimes.Select(r => r.Split(':')).Select(p =>
            (Enum.Parse<RuntimeFamily>(p[0]), p[1] == "missing" ? (int?)null : int.Parse(p[1]))).ToList();
        Assert.Equal(expected, RuntimeSelection.ChoosePrimary(input));
    }
}
