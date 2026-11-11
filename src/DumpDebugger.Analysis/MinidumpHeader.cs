namespace DumpDebugger.Analysis;

/// <summary>
/// Reads the raw MINIDUMP_HEADER and stream directory: whether this is a full-memory dump
/// (§2.3 tier 1 depends on it: only full dumps embed module images, including the DAC), when it
/// was captured, and whether it carries an exception stream (a crash dump rather than a hang
/// snapshot). ClrMD doesn't surface these, so we read them ourselves.
/// </summary>
public static class MinidumpHeader
{
    private const uint MinidumpSignature = 0x504d444d; // "MDMP"
    private const ulong MiniDumpWithFullMemory = 0x00000002;
    private const uint ExceptionStream = 6;

    public sealed record Info(bool IsFullMemory, DateTimeOffset? CapturedAt, bool HasException);

    public static Info Read(string dumpPath)
    {
        using var stream = File.OpenRead(dumpPath);
        using var reader = new BinaryReader(stream);

        if (reader.ReadUInt32() != MinidumpSignature)
        {
            return new Info(false, null, false);
        }

        reader.ReadUInt32(); // Version
        var streamCount = reader.ReadUInt32();
        var directoryRva = reader.ReadUInt32();
        reader.ReadUInt32(); // CheckSum
        var timestamp = reader.ReadUInt32();
        var flags = reader.ReadUInt64();

        var hasException = false;
        for (var i = 0; i < streamCount; i++)
        {
            stream.Position = directoryRva + (i * 12);
            if (reader.ReadUInt32() == ExceptionStream)
            {
                hasException = true;
                break;
            }
        }

        return new Info(
            (flags & MiniDumpWithFullMemory) != 0,
            timestamp == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(timestamp),
            hasException);
    }
}
