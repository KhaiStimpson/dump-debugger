using System.Text.Json;

namespace DumpDebugger.Analysis.SourceLink;

/// <summary>
/// Parses and resolves the Source Link JSON blob embedded in a portable PDB's module-level
/// custom debug information. Spec: https://github.com/dotnet/sourcelink — "documents" maps a
/// wildcarded build-time source path (as it appears in the PDB's Document table) to a
/// wildcarded URL template. The commit SHA is not a placeholder: SourceLink.GitHub and friends
/// bake the exact build commit into the URL template literally when they write this JSON, so
/// resolving a document through this map is enough to recover "this file, at this commit".
/// </summary>
public sealed class SourceLinkMap
{
    private readonly IReadOnlyList<(string Prefix, string UrlPrefix)> _entries;

    private SourceLinkMap(IReadOnlyList<(string Prefix, string UrlPrefix)> entries) => _entries = entries;

    public static SourceLinkMap? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("documents", out var documents))
            {
                return null;
            }

            var entries = new List<(string, string)>();
            foreach (var prop in documents.EnumerateObject())
            {
                var pattern = prop.Name;
                var urlTemplate = prop.Value.GetString();

                // Exact (non-wildcard) entries are valid per spec but rare in practice (every
                // mainstream SourceLink provider package emits a single "*" entry per repo
                // root); skip them rather than special-case an unused shape.
                if (urlTemplate is null || !pattern.EndsWith('*') || !urlTemplate.EndsWith('*'))
                {
                    continue;
                }

                entries.Add((pattern[..^1], urlTemplate[..^1]));
            }

            return entries.Count == 0 ? null : new SourceLinkMap(entries);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Resolves a PDB document name (the original build-time absolute path) to a URL,
    /// picking the longest matching prefix per the Source Link spec.</summary>
    public string? Resolve(string documentName)
    {
        (string Prefix, string UrlPrefix)? best = null;
        foreach (var entry in _entries)
        {
            if (documentName.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase)
                && (best is null || entry.Prefix.Length > best.Value.Prefix.Length))
            {
                best = entry;
            }
        }

        if (best is null)
        {
            return null;
        }

        var suffix = documentName[best.Value.Prefix.Length..].Replace('\\', '/');
        return best.Value.UrlPrefix + suffix;
    }
}
