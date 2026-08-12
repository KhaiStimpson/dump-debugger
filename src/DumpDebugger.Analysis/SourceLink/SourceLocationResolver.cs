using DumpDebugger.Core.Source;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.SourceLink;

/// <summary>
/// Glue between ClrMD (a stack frame's method + native instruction pointer) and the PDB/Source
/// Link machinery in this folder, producing a SourceLocation with zero user configuration —
/// this is what makes dump-to-repo linking automatic rather than something to set up (contrast
/// with RepoContext, which is only needed later, for pulling actual code text for the LLM
/// narrative). One instance should be reused across an entire thread enumeration so the
/// module-level PDB cache in PortablePdbSourceMapper actually pays for itself.
/// </summary>
public sealed class SourceLocationResolver : IDisposable
{
    private readonly PortablePdbSourceMapper _mapper = new();

    public SourceLocation? TryResolve(ClrMethod? method, ulong instructionPointer)
    {
        var modulePath = method?.Type?.Module?.Name;
        if (method is null || modulePath is null)
        {
            return null;
        }

        try
        {
            var ilOffset = method.GetILOffset(instructionPointer);
            if (ilOffset < 0)
            {
                return null;
            }

            if (!_mapper.TryGetSequencePoint(modulePath, method.MetadataToken, ilOffset, out var documentName, out var line, out var column))
            {
                return null;
            }

            var map = _mapper.TryGetSourceLinkMap(modulePath);
            var url = map?.Resolve(documentName);
            if (url is null)
            {
                return null;
            }

            return SourceLocationUrlParser.TryParse(url, out var repoUrl, out var sha, out var relativePath)
                ? new SourceLocation(repoUrl, sha, relativePath, line, column)
                : null;
        }
        catch
        {
            return null; // Best-effort enrichment; a resolution failure must never break stack walking.
        }
    }

    public void Dispose() => _mapper.Dispose();
}
