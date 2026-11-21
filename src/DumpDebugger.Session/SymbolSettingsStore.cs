using System.Text.Json;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Session;

/// <summary>
/// <c>symbols.json</c> under %LOCALAPPDATA%\DumpDebugger: the folders searched for the app's
/// PDBs and source files. Read each time a dump is opened, so dumps an agent opens through the
/// MCP server use the same folders as the one on screen.
/// </summary>
public sealed class SymbolSettingsStore(string? filePath = null)
{
    public const int MaxPaths = 32;

    private readonly string? _path = filePath;

    private string FilePath => _path ?? Path.Combine(AppPaths.Ensure(""), "symbols.json");

    public SymbolSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<SymbolSettings>(File.ReadAllText(FilePath), ContractJson.Options) is { } stored)
            {
                return Normalize(stored);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable settings: carry on with no extra folders.
        }

        return new SymbolSettings([], []);
    }

    public SymbolSettings Save(SymbolSettings settings)
    {
        var clean = Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(clean, ContractJson.Indented));
        File.Move(temp, FilePath, overwrite: true);
        return clean;
    }

    /// <summary>Trimmed, absolute, de-duplicated folders in the order given. Relative paths are dropped: they would mean something different in the worker.</summary>
    public static SymbolSettings Normalize(SymbolSettings settings) =>
        new(Clean(settings.SymbolPaths), Clean(settings.SourcePaths));

    private static List<string> Clean(IReadOnlyList<string>? paths) =>
        (paths ?? [])
            .Select(p => (p ?? "").Trim().Trim('"'))
            .Where(p => p.Length > 0 && IsAbsolute(p))
            .Select(p => p.Length > 3 ? p.TrimEnd('\\', '/') : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPaths)
            .ToList();

    private static bool IsAbsolute(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
