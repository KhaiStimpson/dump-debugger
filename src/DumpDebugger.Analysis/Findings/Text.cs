using System.Globalization;

namespace DumpDebugger.Analysis.Findings;

/// <summary>Helpers for authored finding text. <c>`x`</c> renders as code, <c>{{uri}}</c> as an entity chip.</summary>
public static class Text
{
    public static string Code(string s) => $"`{s}`";

    public static string Chip(string uri) => $"{{{{{uri}}}}}";

    public static string Plural(int n, string one, string? many = null) =>
        $"{n.ToString("N0", CultureInfo.InvariantCulture)} {(n == 1 ? one : many ?? one + "s")}";

    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{bytes} B" : $"{v.ToString(v >= 100 ? "F0" : v >= 10 ? "F1" : "F2", CultureInfo.InvariantCulture)} {units[u]}";
    }

    public static string Percent(double fraction) => $"{Math.Round(fraction * 100).ToString(CultureInfo.InvariantCulture)}%";

    public static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1],
    };

    public static string Sentence(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
