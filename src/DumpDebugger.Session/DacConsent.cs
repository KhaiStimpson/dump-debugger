using DumpDebugger.Core.Dump;

namespace DumpDebugger.Session;

/// <summary>Consent applies only to this open: offline first, at most one prompt and one online retry.</summary>
public static class DacConsent
{
    public static async Task<DumpMetadata> OpenAsync(
        Func<bool, CancellationToken, Task<DumpMetadata>> load,
        Func<DumpMetadata, CancellationToken, Task<bool>> requestDownload,
        CancellationToken ct)
    {
        var metadata = await load(false, ct).ConfigureAwait(false);
        if (metadata.Runtimes.Any(r => !r.IsManagedAnalysisAvailable)
            && await requestDownload(metadata, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            metadata = await load(true, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return metadata;
    }

    public static string? AnalysisError(DumpMetadata metadata)
    {
        if (metadata.Runtimes.Any(r => r.IsManagedAnalysisAvailable)) return null;
        if (metadata.Runtimes.Count == 0) return "This dump contains no supported .NET runtime to analyze.";
        return "Analysis cannot start because no runtime could be loaded. A DAC is a debugging DLL that must match the runtime build exactly."
            + Environment.NewLine + string.Join(Environment.NewLine, metadata.Runtimes.Select(r =>
                $"{r.Family} {r.ProductVersion} (build {r.Version}): {r.DacError ?? "Could not find matching DAC."}"));
    }
}
