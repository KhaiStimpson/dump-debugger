using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Frames;

/// <summary>
/// Maps (method, IL offset) to a source file and line using a portable PDB found next to the
/// module image the dump was captured from. When no PDB is available the frame keeps its
/// method + IL offset (the roadmap's "line numbers need PDBs" fallback).
/// </summary>
public sealed class SourceLocator : IDisposable
{
    private readonly ConcurrentDictionary<string, MetadataReaderProvider?> _pdbs = new(StringComparer.OrdinalIgnoreCase);

    public (string? File, int? Line) Locate(ClrMethod method, int ilOffset)
    {
        var modulePath = method.Type?.Module?.Name;
        if (string.IsNullOrEmpty(modulePath) || method.MetadataToken == 0)
        {
            return (null, null);
        }

        var provider = _pdbs.GetOrAdd(modulePath, Open);
        if (provider is null)
        {
            return (null, null);
        }

        try
        {
            var reader = provider.GetMetadataReader();
            var handle = MetadataTokens.MethodDebugInformationHandle((int)(method.MetadataToken & 0x00ffffff));
            var info = reader.GetMethodDebugInformation(handle);
            SequencePoint? best = null;
            foreach (var sp in info.GetSequencePoints())
            {
                if (sp.IsHidden || sp.Offset > ilOffset)
                {
                    continue;
                }

                if (best is null || sp.Offset >= best.Value.Offset)
                {
                    best = sp;
                }
            }

            if (best is { } point)
            {
                var doc = reader.GetDocument(point.Document);
                var path = reader.GetString(doc.Name);
                return (Path.GetFileName(path), point.StartLine);
            }
        }
        catch
        {
            // Mismatched or non-portable PDB: fall back to IL offset.
        }

        return (null, null);
    }

    private static MetadataReaderProvider? Open(string modulePath)
    {
        try
        {
            var pdb = Path.ChangeExtension(modulePath, ".pdb");
            if (!File.Exists(pdb))
            {
                return null;
            }

            var stream = File.OpenRead(pdb);
            return MetadataReaderProvider.FromPortablePdbStream(stream);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var provider in _pdbs.Values)
        {
            provider?.Dispose();
        }
    }
}
