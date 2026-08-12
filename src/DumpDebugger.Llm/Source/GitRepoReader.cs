using System.Diagnostics;

namespace DumpDebugger.Llm;

/// <summary>
/// Best-effort: shells out to `git show &lt;sha&gt;:&lt;path&gt;` against a local repo clone to
/// read the exact historical file content the dump's build was compiled from — without
/// touching the user's working tree (no checkout, no fetch, no mutation of any kind — read-only
/// against whatever history is already local). If the commit isn't present locally, the path
/// was renamed since, or `git` isn't on PATH, this returns null and the caller just skips that
/// snippet, same "degrade gracefully" posture as everything else source-location-related.
/// </summary>
public static class GitRepoReader
{
    public static async Task<string?> TryReadFileAtCommitAsync(
        string repoLocalPath, string commitSha, string relativePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repoLocalPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("show");
            psi.ArgumentList.Add($"{commitSha}:{relativePath.Replace('\\', '/')}");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            return process.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }
}
