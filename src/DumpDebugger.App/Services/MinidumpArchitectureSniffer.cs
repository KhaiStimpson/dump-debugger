using DumpDebugger.Core.Dump;

namespace DumpDebugger_App.Services;

/// <summary>
/// Reads just enough of the MINIDUMP header/directory/SystemInfo stream to determine the
/// dump's processor architecture, without loading ClrMD. This has to happen before the
/// worker process is launched, since ClrMD requires the analyzing process to already match
/// the dump's architecture (PLAN.md §2.2) — we can't ask ClrMD which worker to start.
/// </summary>
public static class MinidumpArchitectureSniffer
{
    private const uint MinidumpSignature = 0x504d444d; // "MDMP"
    private const uint SystemInfoStreamType = 7;

    public static DumpArchitecture Sniff(string dumpPath)
    {
        using var stream = File.OpenRead(dumpPath);
        using var reader = new BinaryReader(stream);

        var signature = reader.ReadUInt32();
        if (signature != MinidumpSignature)
        {
            throw new InvalidDataException($"'{dumpPath}' is not a MINIDUMP file (bad signature).");
        }

        reader.ReadUInt32(); // Version
        var numberOfStreams = reader.ReadUInt32();
        var streamDirectoryRva = reader.ReadUInt32();

        for (var i = 0; i < numberOfStreams; i++)
        {
            stream.Position = streamDirectoryRva + (i * 12);
            var streamType = reader.ReadUInt32();
            var dataSize = reader.ReadUInt32();
            var rva = reader.ReadUInt32();

            if (streamType != SystemInfoStreamType)
            {
                continue;
            }

            stream.Position = rva;
            var processorArchitecture = reader.ReadUInt16();
            return processorArchitecture switch
            {
                0 => DumpArchitecture.X86,   // PROCESSOR_ARCHITECTURE_INTEL
                9 => DumpArchitecture.X64,   // PROCESSOR_ARCHITECTURE_AMD64
                12 => DumpArchitecture.Arm64, // PROCESSOR_ARCHITECTURE_ARM64
                _ => DumpArchitecture.Unknown,
            };
        }

        throw new InvalidDataException($"'{dumpPath}' has no SystemInfo stream; cannot determine architecture.");
    }
}
