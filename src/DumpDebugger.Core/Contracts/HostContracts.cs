namespace DumpDebugger.Core.Contracts;

/// <summary>Marker for methods that take no parameters.</summary>
public sealed record NoParams;

/// <summary>Marker result for methods that only confirm completion.</summary>
public sealed record Ack(bool Ok = true);

public sealed record AppInfo(string Version, string HostArchitecture, string DataRoot, string? StartupDumpPath);

/// <summary>A rectangle in CSS pixels, relative to the window's client area.</summary>
public sealed record Rect(double X, double Y, double Width, double Height);

/// <summary>Titlebar hit-testing: the strip is draggable except over these interactive rects.</summary>
public sealed record TitlebarRegions(IReadOnlyList<Rect> Passthrough);

public sealed record OpenDumpParams(string Path);

public sealed record ProgressNotification(string Stage, double FractionComplete, string? Detail);

public enum WorkerState
{
    Starting,
    Ready,
    Exited,
}

public sealed record WorkerStatus(WorkerState State, string Architecture, int? ProcessId, string? Message);

public sealed record PickedFile(string? Path);
