namespace DumpDebugger.Analysis;

/// <summary>
/// Reads the raw MINIDUMP_HEADER.Flags to determine whether a dump is a full-memory dump
/// (§2.3 tier 1 depends on this: only full-memory dumps embed loaded module images, including
/// the DAC). ClrMD doesn't surface this directly, so we read it ourselves.
/// </summary>
public static class MinidumpHeader
{
    private const uint MinidumpSignature = 0x504d444d; // "MDMP"
    private const ulong MiniDumpWithFullMemory = 0x00000002;

    public static bool IsFullMemoryDump(string dumpPath)
    {
        using var stream = File.OpenRead(dumpPath);
        using var reader = new BinaryReader(stream);

        if (reader.ReadUInt32() != MinidumpSignature)
        {
            return false;
        }

        stream.Position = 24; // Signature + Version + NumberOfStreams + StreamDirectoryRva + CheckSum + TimeDateStamp
        var flags = reader.ReadUInt64();
        return (flags & MiniDumpWithFullMemory) != 0;
    }
}
