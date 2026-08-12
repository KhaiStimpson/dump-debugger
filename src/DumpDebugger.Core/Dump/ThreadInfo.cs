namespace DumpDebugger.Core.Dump;

public sealed record StackFrameInfo(
    string MethodName,
    string? TypeName);

public sealed record ThreadInfo(
    int OSThreadId,
    int ManagedThreadId,
    bool IsAlive,
    bool IsGc,
    bool IsFinalizer,
    int LockCount,
    string? CurrentExceptionType,
    IReadOnlyList<StackFrameInfo> Frames,
    string StackGroupHash);
