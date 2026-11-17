using DumpDebugger.Core.Dump;
using DumpDebugger.Session;

namespace DumpDebugger.Tests;

public class DacConsentTests
{
    private static DumpMetadata Metadata(params RuntimeInfo[] runtimes) => new(
        "test.dmp", 100, DumpArchitecture.X86, true, false, null, "w3wp.exe", 1, runtimes, 0, 0);

    private static RuntimeInfo Runtime(bool available) => new(RuntimeFamily.NetCore, "10.0.1326.1", "10.0.13", null,
        DacResolutionTier.None, available, available ? null : "Could not find matching DAC");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_dacs_prompt_once_and_only_download_after_consent(bool consent)
    {
        var calls = new List<bool>();
        var prompts = 0;
        var result = await DacConsent.OpenAsync((online, _) =>
        {
            calls.Add(online);
            // Even if online resolution fails there must be no repeated prompt/retry.
            return Task.FromResult(Metadata(Runtime(false)));
        }, (_, _) => { prompts++; return Task.FromResult(consent); }, default);

        Assert.Equal(consent ? new[] { false, true } : new[] { false }, calls);
        Assert.Equal(1, prompts);
        Assert.Contains("Could not find matching DAC", DacConsent.AnalysisError(result));
        Assert.Contains("10.0.13", DacConsent.AnalysisError(result));
    }

    [Fact]
    public async Task Available_runtime_loads_without_prompt_or_network()
    {
        var result = await DacConsent.OpenAsync((online, _) =>
        {
            Assert.False(online);
            return Task.FromResult(Metadata(Runtime(true)));
        }, (_, _) => throw new InvalidOperationException("Unexpected prompt"), default);
        Assert.Null(DacConsent.AnalysisError(result));
    }

    [Fact]
    public async Task Partial_runtime_failure_also_requests_consent_and_retries()
    {
        var result = await DacConsent.OpenAsync((online, _) => Task.FromResult(
            Metadata(Runtime(true), Runtime(online))), (_, _) => Task.FromResult(true), default);
        Assert.All(result.Runtimes, r => Assert.True(r.IsManagedAnalysisAvailable));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_after_prompt_prevents_download(bool consent)
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DacConsent.OpenAsync((online, _) =>
        {
            Assert.False(online);
            return Task.FromResult(Metadata(Runtime(false)));
        }, (_, _) => { cts.Cancel(); return Task.FromResult(consent); }, cts.Token));
    }

    [Fact]
    public async Task Native_dump_has_a_terminal_error_without_a_dac_prompt()
    {
        var result = await DacConsent.OpenAsync((_, _) => Task.FromResult(Metadata()),
            (_, _) => throw new InvalidOperationException("Unexpected prompt"), default);
        Assert.Contains("no supported .NET runtime", DacConsent.AnalysisError(result));
    }
}
