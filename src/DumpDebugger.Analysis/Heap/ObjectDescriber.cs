using System.Globalization;
using System.Text;
using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Locks;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Heap;

/// <summary>
/// Renders objects for people: an identity ("CachedReport \"Q3-statements\"", "PDF document
/// (120 MB)", "List&lt;CartSnapshot&gt; (120,000 items)") and typed field values, instead of
/// <c>byte[] — 84 MB</c> and hex. Everything here reads one object at a time.
/// </summary>
public static class ObjectDescriber
{
    private static readonly string[] IdentityFields = ["Key", "Name", "Id", "Title", "Path", "Url", "Route", "CartId", "OrderId", "Code"];

    public static string Identity(ClrObject obj)
    {
        if (obj.IsNull || !obj.IsValid || obj.Type is null)
        {
            return "null";
        }

        var type = obj.Type;
        try
        {
            if (type.IsString)
            {
                return Quote(obj.AsString(60) ?? "");
            }

            if (type.IsArray)
            {
                var length = obj.AsArray().Length;
                if (type.ComponentType?.ElementType == ClrElementType.UInt8)
                {
                    var sniff = SniffBytes(obj);
                    if (sniff is not null)
                    {
                        return $"{sniff} ({Findings.Text.Bytes((long)obj.Size)})";
                    }
                }

                return $"{FrameClassifier.ShortTypeName(type.ComponentType?.Name)}[{length.ToString("N0", CultureInfo.InvariantCulture)}]";
            }

            var name = type.Name ?? "";
            var shortName = FrameClassifier.ShortTypeName(name);
            if (name.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) || name.StartsWith("System.Collections.Generic.HashSet<", StringComparison.Ordinal))
            {
                return $"{shortName} ({Count(obj, "_size", "_count")} items)";
            }

            if (name.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal))
            {
                var count = ReadInt(obj, "_count") - ReadInt(obj, "_freeCount");
                return $"{shortName} ({count.ToString("N0", CultureInfo.InvariantCulture)} entries)";
            }

            var key = IdentifyingString(obj);
            return key is null ? shortName : $"{shortName} {Quote(key)}";
        }
        catch
        {
            return FrameClassifier.ShortTypeName(type.Name);
        }
    }

    /// <summary>The value of a Key/Name/Id-like string field, if the object has one.</summary>
    public static string? IdentifyingString(ClrObject obj)
    {
        if (obj.Type is null)
        {
            return null;
        }

        foreach (var hint in IdentityFields)
        {
            foreach (var f in obj.Type.Fields)
            {
                if (f.ElementType != ClrElementType.String || f.Name is null)
                {
                    continue;
                }

                var clean = LockModelBuilder.CleanFieldName(f.Name).TrimStart('_');
                if (!clean.Equals(hint, StringComparison.OrdinalIgnoreCase) && !(hint == "Id" && clean.EndsWith("Id", StringComparison.Ordinal)))
                {
                    continue;
                }

                try
                {
                    var s = f.ReadString(obj.Address, false);
                    if (!string.IsNullOrEmpty(s))
                    {
                        return s.Length > 60 ? s[..59] + "…" : s;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    /// <summary>What a byte[] holds, judged by its first bytes.</summary>
    public static string? SniffBytes(ClrObject obj)
    {
        var head = ReadBytes(obj, 16);
        if (head.Length < 4)
        {
            return null;
        }

        if (StartsWith(head, "%PDF"))
        {
            return "PDF document";
        }

        if (head[0] == 0x89 && StartsWith(head.AsSpan(1), "PNG"))
        {
            return "PNG image";
        }

        if (head[0] == 0xFF && head[1] == 0xD8)
        {
            return "JPEG image";
        }

        if (StartsWith(head, "PK\u0003\u0004"))
        {
            return "ZIP archive";
        }

        if (head[0] == 0x1F && head[1] == 0x8B)
        {
            return "GZIP data";
        }

        if (head[0] is (byte)'{' or (byte)'[')
        {
            return "JSON text";
        }

        if (head[0] == (byte)'<')
        {
            return "XML/HTML text";
        }

        return null;
    }

    public static byte[] ReadBytes(ClrObject obj, int max)
    {
        try
        {
            var array = obj.AsArray();
            var n = Math.Min(max, array.Length);
            return n <= 0 ? [] : array.ReadValues<byte>(0, n) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static string HexPreview(byte[] bytes)
    {
        var hex = string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        var ascii = new string(bytes.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
        return $"{hex}  {ascii}";
    }

    public static IReadOnlyList<FieldValue> Fields(ClrObject obj)
    {
        if (obj.Type is null)
        {
            return [];
        }

        return obj.Type.Fields.Select(f => Field(obj.Type.Heap, f, obj.Address, interior: false, depth: 0)).ToList();
    }

    public static FieldValue Field(ClrHeap heap, ClrInstanceField f, ulong address, bool interior, int depth)
    {
        var name = LockModelBuilder.CleanFieldName(f.Name);
        var typeName = FrameClassifier.ShortTypeName(f.Type?.Name ?? f.ElementType.ToString());
        try
        {
            switch (f.ElementType)
            {
                case ClrElementType.String:
                    {
                        var s = f.ReadObject(address, interior);
                        return s.IsNull
                            ? new FieldValue(name, "string", "null", ValueKind.Null, null, Note(name, null), null)
                            : new FieldValue(name, "string", Quote(s.AsString(200) ?? ""), ValueKind.String, s.Address, null, null);
                    }

                case ClrElementType.Class or ClrElementType.Object or ClrElementType.Array or ClrElementType.SZArray:
                    {
                        var child = f.ReadObject(address, interior);
                        if (child.IsNull)
                        {
                            return new FieldValue(name, typeName, "null", ValueKind.Null, null, Note(name, null), null);
                        }

                        return new FieldValue(name, FrameClassifier.ShortTypeName(child.Type?.Name ?? f.Type?.Name), Identity(child), ValueKind.Reference, child.Address, null, null);
                    }

                case ClrElementType.Struct:
                    return Struct(heap, f, address, interior, name, depth);

                case ClrElementType.Pointer or ClrElementType.FunctionPointer or ClrElementType.NativeInt or ClrElementType.NativeUInt:
                    return new FieldValue(name, typeName, $"0x{f.Read<ulong>(address, interior):x}", ValueKind.Pointer, null, null, null);

                default:
                    return Primitive(f, address, interior, name, typeName);
            }
        }
        catch
        {
            return new FieldValue(name, typeName, "<unreadable>", ValueKind.Other, null, null, null);
        }
    }

    private static FieldValue Primitive(ClrInstanceField f, ulong address, bool interior, string name, string typeName)
    {
        (string Display, ValueKind Kind) value = f.ElementType switch
        {
            ClrElementType.Boolean => (f.Read<bool>(address, interior) ? "true" : "false", ValueKind.Bool),
            ClrElementType.Char => ($"'{f.Read<char>(address, interior)}'", ValueKind.Char),
            ClrElementType.Int8 => (f.Read<sbyte>(address, interior).ToString(CultureInfo.InvariantCulture), ValueKind.Number),
            ClrElementType.UInt8 => (f.Read<byte>(address, interior).ToString(CultureInfo.InvariantCulture), ValueKind.Number),
            ClrElementType.Int16 => (f.Read<short>(address, interior).ToString(CultureInfo.InvariantCulture), ValueKind.Number),
            ClrElementType.UInt16 => (f.Read<ushort>(address, interior).ToString(CultureInfo.InvariantCulture), ValueKind.Number),
            ClrElementType.Int32 => (f.Read<int>(address, interior).ToString(CultureInfo.InvariantCulture), ValueKind.Number),
            ClrElementType.UInt32 => (f.Read<uint>(address, interior).ToString(CultureInfo.InvariantCulture), ValueKind.Number),
            ClrElementType.Int64 => (f.Read<long>(address, interior).ToString(CultureInfo.InvariantCulture), ValueKind.Number),
            ClrElementType.UInt64 => (f.Read<ulong>(address, interior).ToString(CultureInfo.InvariantCulture), ValueKind.Number),
            ClrElementType.Float => (f.Read<float>(address, interior).ToString("G", CultureInfo.InvariantCulture), ValueKind.Number),
            ClrElementType.Double => (f.Read<double>(address, interior).ToString("G", CultureInfo.InvariantCulture), ValueKind.Number),
            _ => ($"<{f.ElementType}>", ValueKind.Other),
        };

        if (f.Type?.IsEnum == true)
        {
            typeName = FrameClassifier.ShortTypeName(f.Type.Name);
            value = (value.Display + " (enum)", ValueKind.Number);
        }

        return new FieldValue(name, typeName, value.Display, value.Kind, null, null, null);
    }

    private static FieldValue Struct(ClrHeap heap, ClrInstanceField f, ulong address, bool interior, string name, int depth)
    {
        var vt = f.ReadStruct(address, interior);
        var typeFull = f.Type?.Name ?? "";
        var typeName = FrameClassifier.ShortTypeName(typeFull);
        switch (typeFull)
        {
            case "System.DateTime":
                {
                    var data = vt.ReadField<ulong>("_dateData");
                    var dt = new DateTime((long)(data & 0x3FFFFFFFFFFFFFFF), DateTimeKind.Utc);
                    return new FieldValue(name, "DateTime", dt.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture), ValueKind.DateTime, null, null, null);
                }

            case "System.TimeSpan":
                return new FieldValue(name, "TimeSpan", TimeSpan.FromTicks(vt.ReadField<long>("_ticks")).ToString("c", CultureInfo.InvariantCulture), ValueKind.Number, null, null, null);

            case "System.Guid":
                {
                    var bytes = new byte[16];
                    var reader = heap.Runtime.DataTarget.DataReader;
                    reader.Read(vt.Address, bytes);
                    return new FieldValue(name, "Guid", new Guid(bytes).ToString(), ValueKind.String, null, null, null);
                }

            case "System.Decimal":
                {
                    var bytes = new byte[16];
                    heap.Runtime.DataTarget.DataReader.Read(vt.Address, bytes);
                    var flags = BitConverter.ToInt32(bytes, 0);
                    var hi = BitConverter.ToInt32(bytes, 4);
                    var lo = BitConverter.ToInt64(bytes, 8);
                    var d = new decimal((int)(lo & 0xffffffff), (int)(lo >> 32), hi, flags < 0, (byte)((flags >> 16) & 0xff));
                    return new FieldValue(name, "decimal", d.ToString(CultureInfo.InvariantCulture), ValueKind.Number, null, null, null);
                }
        }

        if (typeFull.StartsWith("System.Nullable<", StringComparison.Ordinal))
        {
            var hasValueField = f.Type!.GetFieldByName("hasValue");
            var valueField = f.Type.GetFieldByName("value");
            if (hasValueField is not null && !hasValueField.Read<bool>(vt.Address, true))
            {
                return new FieldValue(name, typeName.Replace("Nullable<", "").TrimEnd('>') + "?", "null", ValueKind.Null, null, Note(name, null), null);
            }

            if (valueField is not null)
            {
                var inner = Field(heap, valueField, vt.Address, true, depth);
                return inner with { Name = name, TypeName = inner.TypeName + "?" };
            }
        }

        var children = depth < 2 && f.Type is not null
            ? f.Type.Fields.Select(cf => Field(heap, cf, vt.Address, true, depth + 1)).ToList()
            : null;
        return new FieldValue(name, typeName, $"{{{typeName}}}", ValueKind.Struct, null, null, children);
    }

    /// <summary>Elements of an array page. Reference elements are navigable; structs show one level of fields.</summary>
    public static IReadOnlyList<FieldValue> Elements(ClrObject obj, int offset, int limit)
    {
        var type = obj.Type!;
        var component = type.ComponentType;
        var array = obj.AsArray();
        var reader = type.Heap.Runtime.DataTarget.DataReader;
        var pointerSize = reader.PointerSize;
        var dataStart = obj.Address + (ulong)(2 * pointerSize);
        var elementSize = (ulong)type.ComponentSize;
        var end = Math.Min(array.Length, offset + limit);
        var result = new List<FieldValue>();
        for (var i = offset; i < end; i++)
        {
            var at = dataStart + ((ulong)i * elementSize);
            var label = $"[{i}]";
            if (component is null)
            {
                break;
            }

            if (component.IsObjectReference)
            {
                var target = reader.ReadPointer(at);
                var child = target == 0 ? default : type.Heap.GetObject(target);
                result.Add(target == 0
                    ? new FieldValue(label, FrameClassifier.ShortTypeName(component.Name), "null", ValueKind.Null, null, null, null)
                    : new FieldValue(label, FrameClassifier.ShortTypeName(child.Type?.Name), Identity(child), child.Type?.IsString == true ? ValueKind.String : ValueKind.Reference, target, null, null));
            }
            else if (component.IsValueType && component.ElementType == ClrElementType.Struct)
            {
                var children = component.Fields.Select(cf => Field(type.Heap, cf, at, true, 1)).ToList();
                var summary = string.Join(", ", children.Take(3).Select(c => $"{c.Name}: {c.Display}"));
                result.Add(new FieldValue(label, FrameClassifier.ShortTypeName(component.Name), summary, ValueKind.Struct, null, null, children));
            }
            else
            {
                var bytes = new byte[elementSize];
                reader.Read(at, bytes);
                var display = component.ElementType switch
                {
                    ClrElementType.Int32 => BitConverter.ToInt32(bytes).ToString(CultureInfo.InvariantCulture),
                    ClrElementType.Int64 => BitConverter.ToInt64(bytes).ToString(CultureInfo.InvariantCulture),
                    ClrElementType.Char => $"'{BitConverter.ToChar(bytes)}'",
                    ClrElementType.Boolean => bytes[0] != 0 ? "true" : "false",
                    ClrElementType.Double => BitConverter.ToDouble(bytes).ToString("G", CultureInfo.InvariantCulture),
                    _ => string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))),
                };
                result.Add(new FieldValue(label, FrameClassifier.ShortTypeName(component.Name), display, ValueKind.Number, null, null, null));
            }
        }

        return result;
    }

    /// <summary>A plain-English aside for suspicious nulls (the mockup's "null — never expires").</summary>
    private static string? Note(string fieldName, object? value)
    {
        var n = fieldName.ToLowerInvariant();
        if (value is null && (n.Contains("expir") || n.Contains("ttl") || n.Contains("evict")))
        {
            return "never expires";
        }

        return null;
    }

    private static string Quote(string s) => "\"" + (s.Length > 60 ? s[..59] + "…" : s).Replace("\n", "\\n").Replace("\r", "") + "\"";

    private static string Count(ClrObject obj, params string[] fields)
    {
        foreach (var f in fields)
        {
            var field = obj.Type?.GetFieldByName(f);
            if (field is not null)
            {
                return field.Read<int>(obj.Address, false).ToString("N0", CultureInfo.InvariantCulture);
            }
        }

        return "?";
    }

    private static int ReadInt(ClrObject obj, string field) =>
        obj.Type?.GetFieldByName(field) is { } f ? f.Read<int>(obj.Address, false) : 0;

    private static bool StartsWith(ReadOnlySpan<byte> bytes, string ascii) =>
        bytes.Length >= ascii.Length && bytes[..ascii.Length].SequenceEqual(Encoding.ASCII.GetBytes(ascii));
}
