using System.Text;
using System.Text.RegularExpressions;
using DumpDebugger.Core.Contracts;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Frames;

/// <summary>
/// Splits stack frames into app / framework / runtime and renders the short display form the UI
/// leads with (<c>InventoryCache.TryReserve(Sku, int)</c>, not
/// <c>Contoso.Orders.InventoryCache.TryReserve(Contoso.Orders.Sku, Int32)</c>). Runtime helper
/// frames such as <c>InlinedCallFrame</c> are never "where a thread is": that was the v1 bug
/// this exists to fix.
/// </summary>
public static partial class FrameClassifier
{
    private static readonly string[] FrameworkPrefixes =
        ["System.", "Microsoft.", "Internal.", "Windows.", "MS.", "Interop", "netstandard", "mscorlib", "WindowsBase", "Presentation", "DynamicClass", "<CrtImplementationDetails>"];

    public static StackFrame Classify(ClrStackFrame frame, bool isTop, SourceLocator? sources)
    {
        if (frame.Kind != ClrStackFrameKind.ManagedMethod || frame.Method is null)
        {
            var name = (frame.FrameName ?? "runtime frame").TrimStart('.');
            return new StackFrame($"[{name}]", null, FrameKind.Runtime, null, null, null, null, false);
        }

        var method = frame.Method;
        var typeName = method.Type?.Name ?? "";
        var module = ModuleName(method.Type?.Module);
        var kind = KindOf(typeName, module, method.Signature);
        var display = Display(method);

        int? il = null;
        string? file = null;
        int? line = null;
        try
        {
            // Below the top frame the IP is a return address; step back into the call instruction.
            var ip = isTop || frame.InstructionPointer == 0 ? frame.InstructionPointer : frame.InstructionPointer - 1;
            var offset = method.GetILOffset(ip);
            if (offset >= 0)
            {
                il = offset;
                if (kind == FrameKind.App && sources is not null)
                {
                    (file, line) = sources.Locate(method, offset);
                }
            }
        }
        catch
        {
            // IL offsets are best-effort; a frame without one still renders.
        }

        return new StackFrame(display, $"{typeName}.{method.Name}", kind, module, il, file, line, IsWaitFrame(typeName, method.Name));
    }

    public static FrameKind KindOf(string? typeName, string? module, string? signature = null)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return signature?.StartsWith("DynamicClass", StringComparison.Ordinal) == true ? FrameKind.Framework : FrameKind.Runtime;
        }

        if (module is not null && FrameworkPrefixes.Any(p => module.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return FrameKind.Framework;
        }

        return FrameworkPrefixes.Any(p => typeName.StartsWith(p, StringComparison.Ordinal)) ? FrameKind.Framework : FrameKind.App;
    }

    public static bool IsAppType(string? typeName, ClrModule? module) =>
        typeName is not null && KindOf(typeName, ModuleName(module)) == FrameKind.App;

    public static string? ModuleName(ClrModule? module) =>
        string.IsNullOrEmpty(module?.Name) ? null : Path.GetFileNameWithoutExtension(module.Name);

    private static bool IsWaitFrame(string typeName, string? method) => method is not null && (
        (typeName == "System.Threading.Monitor" && method is "Enter" or "Enter_Slowpath" or "ReliableEnter" or "TryEnter" or "Wait")
        || method is "WaitOneNoCheck" or "WaitAnyMultiple" or "SpinThenBlockingWait" or "InternalWaitCore" or "Sleep" or "Join" or "Poll");

    /// <summary>Short, human display for a method: last namespace segment, compiler-generated names unwrapped, C# keyword param types.</summary>
    public static string Display(ClrMethod method)
    {
        var (typePart, methodPart) = Unwrap(method.Type?.Name ?? "", method.Name ?? "?");
        var parameters = ShortParameters(method.Signature);
        return $"{typePart}.{methodPart}({parameters})";
    }

    /// <summary>An async state machine type (<c>Contoso.RatesClient+&lt;GetRateAsync&gt;d__1</c>) as its method: <c>RatesClient.GetRateAsync</c>.</summary>
    public static string AsyncMethodName(string stateMachineType)
    {
        var (typePart, methodPart) = Unwrap(stateMachineType, "MoveNext");
        return $"{typePart}.{methodPart.Replace(" (async)", "")}";
    }

    /// <summary>Display without parameters, for titles: <c>CheckoutService.Commit</c>.</summary>
    public static string DisplayName(ClrMethod method)
    {
        var (typePart, methodPart) = Unwrap(method.Type?.Name ?? "", method.Name ?? "?");
        return $"{typePart}.{methodPart}";
    }

    public static string ShortTypeName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName))
        {
            return "?";
        }

        var (typePart, _) = Unwrap(fullName, "");

        // Generic args belong to the outer type; ignore those of a compiler-generated nested type.
        var cg = fullName.IndexOf("+<", StringComparison.Ordinal);
        var generic = GenericSuffix(cg > 0 ? fullName[..cg] : fullName);
        return Alias(typePart) + generic;
    }

    private static (string Type, string Method) Unwrap(string typeName, string methodName)
    {
        var nested = SplitNested(StripGenerics(typeName));
        var kept = new List<string>();
        var suffix = "";
        foreach (var (part, index) in nested.Select((p, i) => (p, i)))
        {
            var name = index == 0 ? LastSegment(part) : part;
            var asyncMatch = AsyncStateMachine().Match(name);
            if (asyncMatch.Success)
            {
                // <ProcessRequests>d__236.MoveNext → ProcessRequests (async)
                var inner = LocalFunction().Match(asyncMatch.Groups[1].Value);
                methodName = inner.Success ? inner.Groups[1].Value : asyncMatch.Groups[1].Value;
                suffix = " (async)";
                continue;
            }

            if (name.StartsWith("<>c", StringComparison.Ordinal))
            {
                continue; // closure / lambda cache classes
            }

            kept.Add(name);
        }

        var lambda = Lambda().Match(methodName);
        if (lambda.Success)
        {
            methodName = $"{lambda.Groups[1].Value} lambda";
        }
        else
        {
            var local = LocalFunction().Match(methodName);
            if (local.Success)
            {
                methodName = local.Groups[1].Value;
            }
        }

        if (methodName.StartsWith('<') && methodName.EndsWith(">$", StringComparison.Ordinal) || methodName == "<Main>")
        {
            methodName = methodName.Trim('<', '>', '$'); // top-level statements: <Main>$ → Main
        }

        if (methodName == ".ctor")
        {
            methodName = "ctor";
        }

        return (kept.Count > 0 ? string.Join('.', kept) : "?", methodName + suffix);
    }

    private static string ShortParameters(string? signature)
    {
        if (signature is null)
        {
            return "";
        }

        var open = signature.LastIndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close <= open + 1)
        {
            return "";
        }

        var inner = signature[(open + 1)..close];
        return string.Join(", ", SplitTopLevel(inner).Select(ShortParameter));
    }

    private static string ShortParameter(string p)
    {
        p = p.Trim();
        var byRef = p.EndsWith(" ByRef", StringComparison.Ordinal);
        if (byRef)
        {
            p = p[..^6];
        }

        var name = Alias(LastSegment(StripGenerics(p)).Replace('+', '.'));
        var generic = GenericSuffix(p);
        return (byRef ? "ref " : "") + name + generic;
    }

    private static string GenericSuffix(string typeName)
    {
        var lt = typeName.IndexOf('<');
        if (lt < 0 || !typeName.EndsWith('>'))
        {
            return "";
        }

        var args = SplitTopLevel(typeName[(lt + 1)..^1]).Select(a => Alias(LastSegment(StripGenerics(a.Trim())).Replace('+', '.')) + GenericSuffix(a.Trim()));
        return "<" + string.Join(",", args) + ">";
    }

    private static string Alias(string t) => t.EndsWith("[]", StringComparison.Ordinal) ? Alias(t[..^2]) + "[]" : t switch
    {
        "Int32" => "int",
        "Int64" => "long",
        "Int16" => "short",
        "UInt32" => "uint",
        "UInt64" => "ulong",
        "Byte" => "byte",
        "Boolean" => "bool",
        "String" => "string",
        "Object" => "object",
        "Double" => "double",
        "Single" => "float",
        "Char" => "char",
        "Void" => "void",
        "Decimal" => "decimal",
        "__Canon" => "T",
        _ => t,
    };

    /// <summary>
    /// Drops generic argument lists (<c>Task&lt;Int32&gt;</c>, <c>Task`1[[...]]</c>) but keeps
    /// compiler-generated names, which start a name part with '&lt;' (<c>&lt;Main&gt;d__0</c>).
    /// Array brackets (<c>Byte[]</c>) are kept.
    /// </summary>
    private static string StripGenerics(string name)
    {
        var sb = new StringBuilder(name.Length);
        var i = 0;
        while (i < name.Length)
        {
            var c = name[i];
            if (c == '`')
            {
                i++;
                while (i < name.Length && char.IsDigit(name[i]))
                {
                    i++;
                }

                if (i < name.Length && name[i] == '[' && i + 1 < name.Length && name[i + 1] == '[')
                {
                    i = SkipBalanced(name, i, '[', ']');
                }

                continue;
            }

            if (c == '<')
            {
                var end = SkipBalanced(name, i, '<', '>');
                var atPartStart = sb.Length == 0 || sb[^1] is '+' or '.' or '<';
                if (atPartStart)
                {
                    sb.Append(name, i, end - i);
                }

                i = end;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int SkipBalanced(string s, int start, char open, char close)
    {
        var depth = 0;
        for (var i = start; i < s.Length; i++)
        {
            if (s[i] == open)
            {
                depth++;
            }
            else if (s[i] == close && --depth == 0)
            {
                return i + 1;
            }
        }

        return s.Length;
    }

    private static List<string> SplitNested(string typeName)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < typeName.Length; i++)
        {
            switch (typeName[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case '+' when depth == 0:
                    parts.Add(typeName[start..i]);
                    start = i + 1;
                    break;
            }
        }

        parts.Add(typeName[start..]);
        return parts;
    }

    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '<' or '[' or '(':
                    depth++;
                    break;
                case '>' or ']' or ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return s[start..i];
                    start = i + 1;
                    break;
            }
        }

        if (start < s.Length)
        {
            yield return s[start..];
        }
    }

    private static string LastSegment(string name)
    {
        // Don't split inside compiler-generated names like <Main>d__0 or <<Invoke>g__X|1_0>d.
        var depth = 0;
        var cut = -1;
        for (var i = 0; i < name.Length; i++)
        {
            switch (name[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case '.' when depth == 0:
                    cut = i;
                    break;
            }
        }

        return cut >= 0 ? name[(cut + 1)..] : name;
    }

    [GeneratedRegex(@"^<(.+)>d(__\d+)?$")]
    private static partial Regex AsyncStateMachine();

    [GeneratedRegex(@"^<(.+)>b__[\d_]+$")]
    private static partial Regex Lambda();

    [GeneratedRegex(@"g__([^|]+)\|")]
    private static partial Regex LocalFunction();
}
