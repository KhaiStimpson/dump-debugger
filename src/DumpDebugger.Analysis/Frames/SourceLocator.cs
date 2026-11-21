using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Frames;

/// <summary>Where a frame is in source: the file name the UI shows, the line, and the document path recorded in the PDB.</summary>
public readonly record struct SourceLocation(string? File, int? Line, string? Path);

/// <summary>
/// Maps (method, IL offset) to a source file and line using the module's portable PDB, and
/// reads the lines around it. A PDB is looked for, in order: next to the module image the dump
/// was captured from, embedded in that image, at the path the build recorded, and in the
/// configured symbol folders (flat, symbol-store layout, then any subfolder). A candidate is
/// only used when its id matches the one in the module's debug directory, because lines from
/// another build's PDB are worse than no lines. With no PDB the frame keeps its method + IL
/// offset. Windows (native-format) PDBs are recognised and reported, not read.
/// </summary>
public sealed class SourceLocator : IDisposable
{
    private const int MaxIndexedPdbs = 50_000;
    private const int MaxSourceBytes = 8 * 1024 * 1024;

    private static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid Sha256Algorithm = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid Sha1Algorithm = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");

    private readonly object _sync = new();
    private readonly Dictionary<string, Pdb> _pdbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Pdb Pdb, DocumentHandle Handle)> _documents = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _symbolPaths = [];
    private IReadOnlyList<string> _sourcePaths = [];
    private Dictionary<string, List<string>>? _pdbIndex;

    /// <summary>Replaces the search folders and forgets every PDB decision made with the old ones.</summary>
    public void Configure(IReadOnlyList<string>? symbolPaths, IReadOnlyList<string>? sourcePaths)
    {
        lock (_sync)
        {
            _symbolPaths = Clean(symbolPaths);
            _sourcePaths = Clean(sourcePaths);
            Reset();
        }
    }

    public SourceLocation Locate(ClrMethod method, int ilOffset)
    {
        var module = method.Type?.Module;
        if (module is null || method.MetadataToken == 0)
        {
            return default;
        }

        lock (_sync)
        {
            var pdb = Resolve(module);
            if (pdb.Reader is not { } reader)
            {
                return default;
            }

            try
            {
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

                if (best is { } point && !point.Document.IsNil)
                {
                    var path = reader.GetString(reader.GetDocument(point.Document).Name);
                    _documents.TryAdd(path, (pdb, point.Document));
                    return new SourceLocation(FileNameOf(path), point.StartLine, path);
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                // A PDB that doesn't describe this method (trimmed, or a different build that slipped through): fall back to the IL offset.
            }

            return default;
        }
    }

    /// <summary>Whether <paramref name="module"/>'s PDB could be read, and where it was found or looked for.</summary>
    public SymbolModule Describe(ClrModule module)
    {
        lock (_sync)
        {
            var pdb = Resolve(module);
            return new SymbolModule(
                Path.GetFileName(module.Name) is { Length: > 0 } name ? name : "(unnamed module)",
                string.IsNullOrEmpty(module.Name) ? null : module.Name,
                pdb.State,
                pdb.Path,
                pdb.Detail);
        }
    }

    /// <summary>
    /// The lines around <paramref name="query"/>'s line. Only documents a frame has already been
    /// located in are served, so this can't be used to read arbitrary files.
    /// </summary>
    public SourceSnippet Snippet(SourceQuery query)
    {
        lock (_sync)
        {
            if (!_documents.TryGetValue(query.Path, out var known))
            {
                return Unavailable(query, "No frame in this dump points at that file.");
            }

            byte[]? bytes = null;
            var origin = SourceOrigin.None;
            string? resolved = null;
            var modified = false;
            try
            {
                var reader = known.Pdb.Reader!;
                bytes = EmbeddedSource(reader, known.Handle);
                if (bytes is not null)
                {
                    origin = SourceOrigin.Embedded;
                }
                else if (FindOnDisk(query.Path) is { } found)
                {
                    (resolved, origin) = found;
                    var info = new FileInfo(resolved);
                    if (info.Length > MaxSourceBytes)
                    {
                        return Unavailable(query, $"{resolved} is too large to preview.");
                    }

                    bytes = File.ReadAllBytes(resolved);
                    modified = !MatchesChecksum(reader, known.Handle, bytes);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException)
            {
                return Unavailable(query, $"Couldn't read the source: {ex.Message}");
            }

            if (bytes is null)
            {
                return Unavailable(query, _sourcePaths.Count == 0
                    ? "The file isn't on this computer at the path recorded by the build. Add the folder that holds the source code in Symbols & source."
                    : "The file wasn't found at the path recorded by the build or under the source folders.");
            }

            using var text = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var context = Math.Clamp(query.Context, 0, 50);
            var first = Math.Max(1, query.Line - context);
            var last = query.Line + context;
            var lines = new List<SourceLine>();
            for (var number = 1; number <= last && text.ReadLine() is { } line; number++)
            {
                if (number >= first)
                {
                    lines.Add(new SourceLine(number, line.Length > 400 ? line[..400] + "…" : line));
                }
            }

            return lines.Count == 0
                ? Unavailable(query, $"The file has fewer than {query.Line} lines: it isn't the version this build was compiled from.")
                : new SourceSnippet(query.Path, resolved, origin, query.Line, lines, modified, null);
        }
    }

    private static SourceSnippet Unavailable(SourceQuery query, string why) =>
        new(query.Path, null, SourceOrigin.None, query.Line, [], false, why);

    private Pdb Resolve(ClrModule module)
    {
        var key = string.IsNullOrEmpty(module.Name) ? $"@{module.Address:x}" : module.Name;
        if (!_pdbs.TryGetValue(key, out var pdb))
        {
            pdb = Open(module);
            _pdbs[key] = pdb;
        }

        return pdb;
    }

    private Pdb Open(ClrModule module)
    {
        PdbInfo? expected = null;
        try
        {
            expected = module.Pdb;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or BadImageFormatException or ArgumentException)
        {
            // The debug directory isn't readable in this dump: candidates can't be validated, only tried.
        }

        return Open(string.IsNullOrEmpty(module.Name) ? null : module.Name, expected);
    }

    /// <summary>The search itself, for a module known only by its path and debug-directory id (tests call this without a dump).</summary>
    internal SymbolModule Probe(string? modulePath, PdbInfo? expected)
    {
        lock (_sync)
        {
            var pdb = Open(modulePath, expected);
            pdb.Provider?.Dispose();
            return new SymbolModule(Path.GetFileName(modulePath) ?? "", modulePath, pdb.State, pdb.Path, pdb.Detail);
        }
    }

    private Pdb Open(string? modulePath, PdbInfo? expected)
    {
        var pdbName = Path.GetFileName(expected?.Path) is { Length: > 0 } recorded
            ? recorded
            : modulePath is null ? null : Path.ChangeExtension(Path.GetFileName(modulePath), ".pdb");
        if (pdbName is null)
        {
            return new Pdb(SymbolState.NotFound, null, null, null, "The module has no file name (dynamic or in-memory assembly).");
        }

        Pdb? rejected = null;

        Pdb? Try(string? path, bool embedded = false)
        {
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            var result = embedded ? LoadEmbedded(path, expected) : LoadFile(path, expected);
            if (result?.State == SymbolState.Loaded)
            {
                return result;
            }

            // Keep the most informative reason: a wrong build beats a wrong format beats nothing.
            if (result is not null && (rejected is null || result.State == SymbolState.Mismatched))
            {
                rejected = result;
            }

            return null;
        }

        var found = Try(modulePath is null ? null : Path.ChangeExtension(modulePath, ".pdb"))
            ?? Try(modulePath, embedded: true)
            ?? Try(expected?.Path);
        if (found is not null)
        {
            return found;
        }

        foreach (var root in _symbolPaths)
        {
            found = Try(Path.Combine(root, pdbName));
            if (found is null && expected is { } id)
            {
                // Symbol-store layout: <name>.pdb/<guid><age>/<name>.pdb; portable PDBs use FFFFFFFF as the age.
                found = Try(Path.Combine(root, pdbName, $"{id.Guid:N}FFFFFFFF".ToUpperInvariant(), pdbName))
                    ?? Try(Path.Combine(root, pdbName, $"{id.Guid:N}{id.Revision:x}".ToUpperInvariant(), pdbName));
            }

            if (found is not null)
            {
                return found;
            }
        }

        if (PdbIndex().TryGetValue(pdbName, out var candidates))
        {
            foreach (var candidate in candidates)
            {
                if (Try(candidate) is { } match)
                {
                    return match;
                }
            }
        }

        return rejected ?? new Pdb(SymbolState.NotFound, null, null, null, _symbolPaths.Count == 0
            ? $"{pdbName} isn't next to the module or at the path the build recorded. Add the folder that holds it."
            : $"{pdbName} isn't next to the module, at the path the build recorded, or under the symbol folders.");
    }

    private static Pdb? LoadFile(string path, PdbInfo? expected)
    {
        FileStream? stream = null;
        try
        {
            stream = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            var read = stream.Read(magic);
            stream.Position = 0;
            if (read < 4 || magic[0] != (byte)'B' || magic[1] != (byte)'S' || magic[2] != (byte)'J' || magic[3] != (byte)'B')
            {
                stream.Dispose();
                return new Pdb(SymbolState.Unsupported, path, null, null,
                    "This is a Windows (native-format) PDB. Rebuild with <DebugType>portable</DebugType> or <DebugType>embedded</DebugType> to get line numbers.");
            }

            var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            stream = null; // the provider owns it now
            return Accept(provider, path, expected, "Portable PDB");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            stream?.Dispose();
            return null;
        }
    }

    private static Pdb? LoadEmbedded(string modulePath, PdbInfo? expected)
    {
        try
        {
            using var pe = new PEReader(File.OpenRead(modulePath));
            foreach (var entry in pe.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    return Accept(pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry), modulePath, expected, "Portable PDB embedded in the module");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or InvalidDataException)
        {
            // Not a readable PE file, or no usable embedded PDB.
        }

        return null;
    }

    private static Pdb Accept(MetadataReaderProvider provider, string path, PdbInfo? expected, string what)
    {
        try
        {
            var reader = provider.GetMetadataReader();
            if (expected is { } id && id.Guid != Guid.Empty && reader.DebugMetadataHeader is { } header)
            {
                var actual = new BlobContentId(header.Id).Guid;
                if (actual != id.Guid)
                {
                    provider.Dispose();
                    return new Pdb(SymbolState.Mismatched, path, null, null,
                        "This PDB is from a different build of the module, so its line numbers would be wrong. Use the PDB produced by the same build that was deployed.");
                }
            }

            return new Pdb(SymbolState.Loaded, path, provider, reader, what);
        }
        catch (BadImageFormatException)
        {
            provider.Dispose();
            return new Pdb(SymbolState.Unsupported, path, null, null, "The PDB is damaged or isn't a portable PDB.");
        }
    }

    /// <summary>Every .pdb under the symbol folders by file name, built the first time the direct lookups miss.</summary>
    private Dictionary<string, List<string>> PdbIndex()
    {
        if (_pdbIndex is not null)
        {
            return _pdbIndex;
        }

        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 12, AttributesToSkip = FileAttributes.ReparsePoint };
        var total = 0;
        foreach (var root in _symbolPaths)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.pdb", options))
                {
                    var name = Path.GetFileName(file);
                    if (!index.TryGetValue(name, out var list))
                    {
                        index[name] = list = [];
                    }

                    list.Add(file);
                    if (++total >= MaxIndexedPdbs)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that went away or can't be listed contributes nothing.
            }

            if (total >= MaxIndexedPdbs)
            {
                break;
            }
        }

        // Newest first: with several builds on disk, the latest is the likeliest match (the id check decides).
        foreach (var list in index.Values)
        {
            list.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
        }

        return _pdbIndex = index;
    }

    private static byte[]? EmbeddedSource(MetadataReader reader, DocumentHandle document)
    {
        foreach (var handle in reader.GetCustomDebugInformation(document))
        {
            var info = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(info.Kind) != EmbeddedSourceKind)
            {
                continue;
            }

            var blob = reader.GetBlobBytes(info.Value);
            if (blob.Length < sizeof(int))
            {
                return null;
            }

            // int32 uncompressed size, then the bytes: raw when the size is 0, deflate otherwise.
            var size = BitConverter.ToInt32(blob, 0);
            if (size == 0)
            {
                return blob[sizeof(int)..];
            }

            if (size < 0 || size > MaxSourceBytes)
            {
                return null;
            }

            using var deflate = new DeflateStream(new MemoryStream(blob, sizeof(int), blob.Length - sizeof(int)), CompressionMode.Decompress);
            var output = new byte[size];
            deflate.ReadExactly(output);
            return output;
        }

        return null;
    }

    private (string Path, SourceOrigin Origin)? FindOnDisk(string documentPath)
    {
        if (Path.IsPathFullyQualified(documentPath) && File.Exists(documentPath))
        {
            return (documentPath, SourceOrigin.Disk);
        }

        // Build paths rarely exist here (/_/src/Orders/Ledger.cs, D:\a\1\s\...): try ever shorter
        // tails of the recorded path under each source folder, longest (most specific) first.
        var parts = documentPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var root in _sourcePaths)
        {
            for (var skip = 0; skip < parts.Length; skip++)
            {
                if (parts[skip].Contains(':') || parts[skip] == "..")
                {
                    continue;
                }

                var candidate = Path.Combine([root, .. parts[skip..]]);
                if (File.Exists(candidate))
                {
                    return (candidate, SourceOrigin.SourceFolder);
                }
            }
        }

        return null;
    }

    private static bool MatchesChecksum(MetadataReader reader, DocumentHandle handle, byte[] content)
    {
        var document = reader.GetDocument(handle);
        if (document.Hash.IsNil || document.HashAlgorithm.IsNil)
        {
            return true;
        }

        var algorithm = reader.GetGuid(document.HashAlgorithm);
        var recorded = reader.GetBlobBytes(document.Hash);
        byte[]? actual = algorithm == Sha256Algorithm ? SHA256.HashData(content)
            : algorithm == Sha1Algorithm ? SHA1.HashData(content)
            : null;
        return actual is null || actual.AsSpan().SequenceEqual(recorded);
    }

    private static string FileNameOf(string path)
    {
        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    private static List<string> Clean(IReadOnlyList<string>? paths) =>
        (paths ?? [])
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0 && Path.IsPathFullyQualified(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void Reset()
    {
        foreach (var pdb in _pdbs.Values)
        {
            pdb.Provider?.Dispose();
        }

        _pdbs.Clear();
        _documents.Clear();
        _pdbIndex = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            Reset();
        }
    }

    private sealed record Pdb(SymbolState State, string? Path, MetadataReaderProvider? Provider, MetadataReader? Reader, string Detail);
}
