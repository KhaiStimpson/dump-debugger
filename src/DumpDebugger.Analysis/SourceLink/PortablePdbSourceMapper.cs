using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace DumpDebugger.Analysis.SourceLink;

/// <summary>
/// Opens the portable PDB for a module — embedded in the DLL, or a standalone .pdb sitting next
/// to it — and maps a method token + IL offset to a source document, line, and column. This is
/// the mechanism behind automatic dump-to-repo linking (see SourceLocationResolver): it only
/// works when the module file is reachable on the analyzing machine's disk (true for a dump of
/// your own locally-built app, often false for one captured on a server and copied over — in
/// that case every lookup here just returns false and callers degrade to "no source location",
/// same as an unresolved DAC).
///
/// Caches one MetadataReader per module path for the life of the instance, since a dump's
/// threads share modules heavily (hundreds of frames, a handful of distinct modules).
/// </summary>
public sealed class PortablePdbSourceMapper : IDisposable
{
    // Well-known Source Link custom debug information kind (dotnet/sourcelink spec).
    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    private readonly Dictionary<string, ModuleDebugInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetSequencePoint(
        string modulePath, int methodToken, int ilOffset, out string documentName, out int line, out int column)
    {
        documentName = "";
        line = 0;
        column = 0;

        var info = GetOrLoad(modulePath);
        if (info is null)
        {
            return false;
        }

        try
        {
            var handle = (MethodDefinitionHandle)MetadataTokens.Handle(methodToken);
            var debugHandle = handle.ToDebugInformationHandle();
            if (debugHandle.IsNil)
            {
                return false;
            }

            var debugInfo = info.Reader.GetMethodDebugInformation(debugHandle);

            // Sequence points are emitted in IL-offset order; the "current" one for a given
            // offset is the last non-hidden point at or before it.
            SequencePoint? best = null;
            foreach (var point in debugInfo.GetSequencePoints())
            {
                if (point.Offset > ilOffset)
                {
                    break;
                }

                if (point.StartLine != SequencePoint.HiddenLine)
                {
                    best = point;
                }
            }

            if (best is not { } sp)
            {
                return false;
            }

            var document = info.Reader.GetDocument(sp.Document);
            documentName = info.Reader.GetString(document.Name);
            line = sp.StartLine;
            column = sp.StartColumn;
            return true;
        }
        catch
        {
            return false; // Method not in this PDB (partial/mismatched pdb), bad token, etc.
        }
    }

    public SourceLinkMap? TryGetSourceLinkMap(string modulePath) => GetOrLoad(modulePath)?.SourceLinkMap;

    private ModuleDebugInfo? GetOrLoad(string modulePath)
    {
        if (_cache.TryGetValue(modulePath, out var cached))
        {
            return cached;
        }

        var info = TryLoad(modulePath);
        _cache[modulePath] = info;
        return info;
    }

    private static ModuleDebugInfo? TryLoad(string modulePath)
    {
        if (string.IsNullOrEmpty(modulePath) || !File.Exists(modulePath))
        {
            return null;
        }

        try
        {
            using var moduleStream = File.OpenRead(modulePath);
            using var peReader = new PEReader(moduleStream);

            var provider = TryReadEmbeddedPdb(peReader) ?? TryReadStandalonePdb(peReader, modulePath);
            if (provider is null)
            {
                return null;
            }

            var reader = provider.GetMetadataReader();
            var sourceLinkMap = TryReadSourceLink(reader);
            return new ModuleDebugInfo(provider, reader, sourceLinkMap);
        }
        catch
        {
            return null; // Not a managed PE, corrupt, no debug directory — the expected common case.
        }
    }

    private static MetadataReaderProvider? TryReadEmbeddedPdb(PEReader peReader)
    {
        foreach (var entry in peReader.ReadDebugDirectory())
        {
            if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
            {
                return peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
            }
        }

        return null;
    }

    private static MetadataReaderProvider? TryReadStandalonePdb(PEReader peReader, string modulePath)
    {
        foreach (var entry in peReader.ReadDebugDirectory())
        {
            if (entry.Type != DebugDirectoryEntryType.CodeView)
            {
                continue;
            }

            var codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
            var moduleDir = Path.GetDirectoryName(modulePath);
            if (moduleDir is null)
            {
                return null;
            }

            var pdbPath = Path.Combine(moduleDir, Path.GetFileName(codeView.Path));
            if (!File.Exists(pdbPath) || !IsPortablePdb(pdbPath))
            {
                return null; // Classic (non-portable) PDB, or none shipped next to the module.
            }

            // Ownership of this stream transfers to the provider (disposed via ModuleDebugInfo).
            var pdbStream = File.OpenRead(pdbPath);
            return MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        }

        return null;
    }

    private static bool IsPortablePdb(string pdbPath)
    {
        using var stream = File.OpenRead(pdbPath);
        Span<byte> magic = stackalloc byte[4];
        return stream.Read(magic) == 4 && magic[0] == 'B' && magic[1] == 'S' && magic[2] == 'J' && magic[3] == 'B';
    }

    private static SourceLinkMap? TryReadSourceLink(MetadataReader reader)
    {
        foreach (var handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var cdi = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(cdi.Kind) != SourceLinkKind)
            {
                continue;
            }

            var bytes = reader.GetBlobBytes(cdi.Value);
            return SourceLinkMap.TryParse(Encoding.UTF8.GetString(bytes));
        }

        return null;
    }

    public void Dispose()
    {
        foreach (var info in _cache.Values)
        {
            info?.Provider.Dispose();
        }

        _cache.Clear();
    }

    private sealed record ModuleDebugInfo(MetadataReaderProvider Provider, MetadataReader Reader, SourceLinkMap? SourceLinkMap);
}
