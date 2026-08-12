using DumpDebugger.Core.Dump;

namespace DumpDebugger_App.ViewModels;

public sealed record TypeStatRow(string TypeName, int Count, string TotalSizeDisplay)
{
    public static TypeStatRow From(TypeStat stat) =>
        new(stat.TypeName, stat.Count, FormatBytes(stat.TotalSize));

    private static string FormatBytes(ulong bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB" : $"{bytes / 1024.0:F1} KB";
}

public sealed record LargeObjectRow(string TypeName, string SizeDisplay, string Address, string Identity)
{
    public static LargeObjectRow From(LargeObjectInfo obj) => new(
        obj.TypeName,
        obj.Size >= 1024 * 1024 ? $"{obj.Size / 1024.0 / 1024.0:F1} MB" : $"{obj.Size / 1024.0:F1} KB",
        $"0x{obj.Address:x}",
        obj.IdentifyingFields.Count == 0
            ? "(no identifying fields)"
            : string.Join(", ", obj.IdentifyingFields.Select(f => $"{f.FieldName}={f.Value}")));
}
