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

    /// <summary>Blames a single line as of a specific commit: who last touched it, and with what
    /// commit message. Same read-only, no-mutation posture as TryReadFileAtCommitAsync.</summary>
    public static async Task<BlameInfo?> TryGetBlameAsync(
        string repoLocalPath, string commitSha, string relativePath, int line, CancellationToken ct)
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
            psi.ArgumentList.Add("blame");
            psi.ArgumentList.Add("--porcelain");
            psi.ArgumentList.Add("-L");
            psi.ArgumentList.Add($"{line},{line}");
            psi.ArgumentList.Add(commitSha);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(relativePath.Replace('\\', '/'));

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            return process.ExitCode == 0 ? ParsePorcelainBlame(stdout) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses `git blame --porcelain -L N,N` output: a header line
    /// "&lt;sha&gt; &lt;origLine&gt; &lt;finalLine&gt; [&lt;count&gt;]", then "author "/"author-time
    /// "/"summary " lines (plus others this doesn't need), terminated by the tab-prefixed source
    /// line itself.</summary>
    private static BlameInfo? ParsePorcelainBlame(string porcelain)
    {
        string? sha = null;
        string? author = null;
        string? summary = null;
        long? authorTimeUnix = null;

        using var reader = new StringReader(porcelain);
        var first = true;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (first)
            {
                sha = line.Split(' ', 2)[0];
                first = false;
                continue;
            }

            if (line.StartsWith("author ", StringComparison.Ordinal))
            {
                author = line["author ".Length..];
            }
            else if (line.StartsWith("author-time ", StringComparison.Ordinal)
                && long.TryParse(line["author-time ".Length..], out var t))
            {
                authorTimeUnix = t;
            }
            else if (line.StartsWith("summary ", StringComparison.Ordinal))
            {
                summary = line["summary ".Length..];
            }
            else if (line.StartsWith('\t'))
            {
                break; // The blamed source line's content ends the porcelain header block.
            }
        }

        if (sha is null || author is null || authorTimeUnix is null || summary is null)
        {
            return null;
        }

        return new BlameInfo(sha, author, DateTimeOffset.FromUnixTimeSeconds(authorTimeUnix.Value), summary);
    }
}
