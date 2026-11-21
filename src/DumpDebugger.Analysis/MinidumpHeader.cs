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
    private const uint ThreadInfoListStream = 17;

    public sealed record Info(bool IsFullMemory, DateTimeOffset? CapturedAt, bool HasException, int? CrashThreadOsId = null, uint? ExceptionCode = null);

    /// <summary>What the operating system recorded about one thread: when it started and how much CPU it has used since.</summary>
    public sealed record ThreadTime(DateTimeOffset? Created, TimeSpan Kernel, TimeSpan User)
    {
        public TimeSpan Cpu => Kernel + User;
    }

    /// <summary>
    /// Per-thread CPU times by OS thread id, from the MINIDUMP_THREAD_INFO_LIST stream. Empty when
    /// the dump has none (a core dump, or a minidump written without thread info).
    /// </summary>
    public static IReadOnlyDictionary<int, ThreadTime> ReadThreadTimes(string dumpPath)
    {
        var result = new Dictionary<int, ThreadTime>();
        try
        {
            using var stream = File.OpenRead(dumpPath);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != MinidumpSignature)
            {
                return result;
            }

            reader.ReadUInt32(); // Version
            var streamCount = reader.ReadUInt32();
            var directoryRva = reader.ReadUInt32();
            for (var i = 0; i < streamCount; i++)
            {
                stream.Position = directoryRva + (i * 12);
                if (reader.ReadUInt32() != ThreadInfoListStream)
                {
                    continue;
                }

                reader.ReadUInt32(); // DataSize
                var rva = reader.ReadUInt32();
                stream.Position = rva;
                var headerSize = reader.ReadUInt32();
                var entrySize = reader.ReadUInt32();
                var count = reader.ReadUInt32();
                if (entrySize < 48 || count > 1_000_000)
                {
                    break;
                }

                for (var n = 0; n < count; n++)
                {
                    // MINIDUMP_THREAD_INFO: ThreadId, DumpFlags, DumpError, ExitStatus, then four 100 ns FILETIME values.
                    stream.Position = rva + headerSize + ((long)n * entrySize);
                    var threadId = (int)reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadUInt32();
                    var created = reader.ReadInt64();
                    reader.ReadInt64(); // ExitTime
                    var kernel = reader.ReadInt64();
                    var user = reader.ReadInt64();
                    if (kernel < 0 || user < 0)
                    {
                        continue;
                    }

                    DateTimeOffset? createdAt = null;
                    try
                    {
                        createdAt = created > 0 ? DateTimeOffset.FromFileTime(created) : null;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                    }

                    result[threadId] = new ThreadTime(createdAt, TimeSpan.FromTicks(kernel), TimeSpan.FromTicks(user));
                }

                break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Thread times are a bonus: a dump that can't give them is still analyzed.
        }

        return result;
    }

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
        int? crashThread = null;
        uint? code = null;
        for (var i = 0; i < streamCount; i++)
        {
            stream.Position = directoryRva + (i * 12);
            if (reader.ReadUInt32() == ExceptionStream)
            {
                reader.ReadUInt32(); // DataSize
                var rva = reader.ReadUInt32();
                stream.Position = rva;
                crashThread = (int)reader.ReadUInt32(); // MINIDUMP_EXCEPTION_STREAM.ThreadId
                reader.ReadUInt32();                    // alignment
                code = reader.ReadUInt32();             // ExceptionRecord.ExceptionCode
                hasException = true;
                break;
            }
        }

        return new Info(
            (flags & MiniDumpWithFullMemory) != 0,
            timestamp == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(timestamp),
            hasException,
            crashThread,
            code);
    }
}
