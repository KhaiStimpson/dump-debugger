using System.Diagnostics;
using DumpDebugger.Core.Source;

namespace DumpDebugger_App.Services;

/// <summary>
/// "Clickable frames": opens a resolved SourceLocation either in a local editor (when a repo is
/// associated and VS Code is on PATH) or in the browser at the exact commit — always available
/// as a fallback, since SourceLocation's RepoUrl/CommitSha/RelativePath alone are enough to
/// build a GitHub blob URL with no local repo clone needed at all.
/// </summary>
public static class SourceLocationLauncher
{
    public static void Open(SourceLocation location, RepoContext? repo)
    {
        if (repo is not null)
        {
            var localPath = repo.ResolveLocalPath(location);
            if (File.Exists(localPath) && TryOpenInEditor(localPath, location.Line))
            {
                return;
            }
        }

        OpenUrl(location.ToGitHubBlobUrl());
    }

    private static bool TryOpenInEditor(string path, int line)
    {
        try
        {
            var psi = new ProcessStartInfo("code")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--goto");
            psi.ArgumentList.Add($"{path}:{line}");

            using var process = Process.Start(psi);
            return process is not null;
        }
        catch
        {
            return false; // `code` not on PATH, or the launch otherwise failed — fall back to the browser.
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort; nothing sensible to do if the OS can't shell out to a browser/handler.
        }
    }
}
