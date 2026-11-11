using DumpDebugger.Core.Rpc;

namespace DumpDebugger.Core.Contracts;

public enum EntityKind
{
    Thread,
    Lock,
    Object,
    Type,
    Request,
    Finding,
}

/// <summary>
/// A link to anything the app can show: rendered as one chip style everywhere and opened in the
/// inspector on click. <see cref="Uri"/> is stable for the dump (see <see cref="EntityUri"/>),
/// so it also keys pins, notes, inspector history and report links.
/// </summary>
[TsDoc("A link to any entity. `uri` is stable per dump: thread/{id}, lock/0x{addr}, object/0x{addr}, type/{name}, request/{route}, finding/{id}.")]
public sealed record EntityRef(string Uri, EntityKind Kind, string Label, string? Detail = null);

/// <summary>
/// Entity URI scheme: <c>thread/{managedId}</c>, <c>lock/0x{addr}</c>, <c>object/0x{addr}</c>,
/// <c>type/{escaped name}</c>, <c>request/{escaped route}</c>, <c>finding/{id}</c>.
/// Mirrored in <c>web/src/entities.ts</c>.
/// </summary>
public static class EntityUri
{
    public static string Thread(int managedId) => $"thread/{managedId}";

    public static string Lock(ulong address) => $"lock/0x{address:x}";

    public static string Object(ulong address) => $"object/0x{address:x}";

    public static string Type(string typeName) => $"type/{System.Uri.EscapeDataString(typeName)}";

    public static string Request(string route) => $"request/{System.Uri.EscapeDataString(route)}";

    public static string Finding(string id) => $"finding/{id}";

    public static EntityRef ThreadRef(int managedId, string? detail = null) =>
        new(Thread(managedId), EntityKind.Thread, $"#{managedId}", detail);

    public static EntityRef LockRef(ulong address, string name, string? detail = null) =>
        new(Lock(address), EntityKind.Lock, name, detail);

    public static EntityRef ObjectRef(ulong address, string label, string? detail = null) =>
        new(Object(address), EntityKind.Object, label, detail);

    public static EntityRef TypeRef(string typeName, string? label = null, string? detail = null) =>
        new(Type(typeName), EntityKind.Type, label ?? typeName, detail);

    public static EntityRef RequestRef(string route, string? detail = null) =>
        new(Request(route), EntityKind.Request, route, detail);

    /// <summary>Parses <c>object/0x1234</c> or <c>lock/0x1234</c> into an address.</summary>
    public static bool TryGetAddress(string uri, out ulong address)
    {
        address = 0;
        var slash = uri.IndexOf('/');
        if (slash < 0)
        {
            return false;
        }

        var hex = uri[(slash + 1)..];
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out address);
    }
}
