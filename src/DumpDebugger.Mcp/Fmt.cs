using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DumpDebugger.Core.Contracts;
using ModelContextProtocol;

namespace DumpDebugger.Mcp;

/// <summary>Small text helpers for tool output: plain lines a model reads well, with sizes and caps.</summary>
internal static partial class Fmt
{
    /// <summary>Longest run of text taken from dump memory (a string value, a field preview) in one place.</summary>
    public const int MaxValueLength = 300;

    public static string Bytes(long bytes)
    {
        var sign = bytes < 0 ? "-" : "";
        double value = Math.Abs(bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return sign + (unit == 0 ? $"{value:0} B" : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}"));
    }

    public static string Signed(long value) => value > 0 ? $"+{value:N0}" : value.ToString("N0", CultureInfo.InvariantCulture);

    public static string SignedBytes(long bytes) => bytes > 0 ? "+" + Bytes(bytes) : Bytes(bytes);

    public static string Percent(double share) => string.Create(CultureInfo.InvariantCulture, $"{share * 100:0.#}%");

    public static string Hex(ulong address) => $"0x{address:x}";

    /// <summary>Text read out of the dump: single line, capped, so one huge string can't flood the reply.</summary>
    public static string Value(string? text, int max = MaxValueLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var flat = text.Replace("\r", "\\r").Replace("\n", "\\n");
        return flat.Length <= max ? flat : $"{flat[..max]}… (+{flat.Length - max:N0} chars)";
    }

    /// <summary>Finding text marks: <c>{{uri}}</c> becomes the entity's label followed by its URI; <c>`code`</c> stays.</summary>
    public static string Marks(string text, IReadOnlyList<EntityRef> entities) =>
        MarkPattern().Replace(text, m =>
        {
            var uri = m.Groups[1].Value;
            var label = entities.FirstOrDefault(e => e.Uri == uri)?.Label;
            return label is null || label == uri ? uri : $"{label} ({uri})";
        });

    public static string Entity(EntityRef e) =>
        e.Detail is { Length: > 0 } ? $"{e.Label} ({e.Uri}; {Value(e.Detail, 120)})" : $"{e.Label} ({e.Uri})";

    /// <summary>Accepts <c>0x1f3a</c>, <c>1f3a</c>, or an <c>object/0x1f3a</c> / <c>lock/0x1f3a</c> URI.</summary>
    public static ulong Address(string address)
    {
        var text = address.Trim();
        var slash = text.LastIndexOf('/');
        if (slash >= 0)
        {
            text = text[(slash + 1)..];
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) && value != 0
            ? value
            : throw new McpException($"'{address}' isn't an object address. Pass a hex address such as 0x1f3a5c8 or a URI such as object/0x1f3a5c8.");
    }

    public static T Choice<T>(string? value, T fallback, string parameter)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Replace("-", "").Replace("_", "").Replace(" ", "");
        return Enum.TryParse<T>(normalized, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new McpException($"`{parameter}` must be one of: {string.Join(", ", Enum.GetNames<T>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}.");
    }

    public static string Lower<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static void Paging(StringBuilder sb, int offset, int shown, int total, string what)
    {
        if (total == 0)
        {
            sb.AppendLine($"No {what}.");
        }
        else if (offset + shown < total)
        {
            sb.AppendLine($"Showing {offset + 1}-{offset + shown} of {total:N0} {what}. Pass offset={offset + shown} for the next page.");
        }
        else
        {
            sb.AppendLine($"Showing {offset + 1}-{offset + shown} of {total:N0} {what} (end of list).");
        }
    }

    [GeneratedRegex(@"\{\{([^}]+)\}\}")]
    private static partial Regex MarkPattern();
}
