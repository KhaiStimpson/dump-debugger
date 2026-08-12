using DumpDebugger.Core.Dump;

namespace DumpDebugger.Core.Ipc;

/// <summary>
/// Wire contract between DumpDebugger.App and a DumpDebugger.Worker process, carried over a
/// named pipe as length-prefixed JSON (see IpcFrame). One pipe per worker, request/response
/// correlated by RequestId, with unsolicited Progress frames pushed during long operations.
/// </summary>
public enum IpcMessageKind
{
    OpenDumpRequest,
    OpenDumpResponse,
    GetThreadsRequest,
    GetThreadsResponse,
    GetLocksRequest,
    GetLocksResponse,
    ProgressNotification,
    CancelRequest,
    ErrorResponse,
}

public sealed record IpcEnvelope(
    IpcMessageKind Kind,
    string RequestId,
    string PayloadJson);

public sealed record OpenDumpRequest(string DumpPath);

public sealed record OpenDumpResponse(DumpMetadata Metadata);

/// <summary>No parameters: the worker keeps the most recently opened dump loaded for the life of the pipe connection.</summary>
public sealed record GetThreadsRequest;

public sealed record GetThreadsResponse(IReadOnlyList<ThreadInfo> Threads);

public sealed record GetLocksRequest;

public sealed record GetLocksResponse(
    IReadOnlyList<SyncBlockInfo> SyncBlocks,
    IReadOnlyList<Findings.Finding> Findings);

public sealed record ProgressNotification(string Stage, double FractionComplete, string? Detail);

public sealed record ErrorResponse(string Message, string? Details);
