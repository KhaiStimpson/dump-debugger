using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Session;

/// <summary>An <see cref="IHostPlatform"/> with no window: dialogs return preset answers. Used by tests.</summary>
public sealed class HeadlessPlatform : IHostPlatform
{
    public string? NextPickedDump { get; set; }

    public string? NextSavePath { get; set; }

    public Task<string?> PickDumpFileAsync() => Task.FromResult(NextPickedDump);

    public Task<string?> PickSaveFileAsync(string suggestedName, string extension) => Task.FromResult(NextSavePath);

    public void SetTitlebarPassthrough(IReadOnlyList<Rect> rects)
    {
    }
}
