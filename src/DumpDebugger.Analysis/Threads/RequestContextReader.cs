using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis.Threads;

public sealed record RequestContext(string Method, string Path, string Route);

/// <summary>
/// Works out which HTTP request a thread is serving from the objects on its stack (PLAN.md §4.1
/// "Operation context"). ASP.NET Core: a Kestrel <c>Http1Connection</c>/<c>Http2Stream</c>
/// carries <c>Method</c> and <c>Path</c>; a <c>DefaultHttpContext</c> or
/// <c>DefaultHttpRequest/Response</c> leads to it through its feature collection. .NET
/// Framework: <c>System.Web.HttpContext</c> → request strings. Paths are normalized to a route
/// shape (<c>/api/orders/{id}/notes</c>) so requests group.
/// </summary>
public static partial class RequestContextReader
{
    // Kestrel's internal HttpMethod enum (Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpMethod).
    private static readonly string[] KestrelMethods = ["GET", "PUT", "DELETE", "POST", "HEAD", "TRACE", "PATCH", "CONNECT", "OPTIONS"];

    public static RequestContext? Read(ClrThread thread, IReadOnlyList<StackRootObject> roots)
    {
        var heap = thread.Runtime.Heap;
        foreach (var root in roots)
        {
            var type = root.TypeName;
            if (type is null || !type.Contains("Http", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var obj = heap.GetObject(root.Address);
                var ctx = FromObject(obj, 0);
                if (ctx is not null)
                {
                    return ctx;
                }
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    public static RequestContext? FromObject(ClrObject obj, int depth)
    {
        if (!obj.IsValid || obj.Type?.Name is not { } type || depth > 3)
        {
            return null;
        }

        if (type.StartsWith("Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http", StringComparison.Ordinal)
            && (type.Contains("Http1Connection", StringComparison.Ordinal) || type.Contains("Http2Stream", StringComparison.Ordinal) || type.Contains("Http3Stream", StringComparison.Ordinal)))
        {
            return ReadKestrel(obj);
        }

        if (type == "Microsoft.AspNetCore.Http.DefaultHttpContext")
        {
            var features = obj.Type.GetFieldByName("_features");
            if (features is not null)
            {
                var vt = features.ReadStruct(obj.Address, interior: false);
                var collection = vt.ReadObjectField("<Collection>k__BackingField");
                return FromObject(collection, depth + 1);
            }
        }

        if (type is "Microsoft.AspNetCore.Http.DefaultHttpRequest" or "Microsoft.AspNetCore.Http.DefaultHttpResponse")
        {
            return obj.TryReadObjectField("_context", out var context) ? FromObject(context, depth + 1) : null;
        }

        if (type == "System.Web.HttpContext")
        {
            if (obj.TryReadObjectField("_request", out var request) && request.IsValid)
            {
                var method = TryString(request, "_httpMethod") ?? "?";
                var path = TryString(request, "_rawUrl") ?? TryString(request, "_filePath") ?? "?";
                return Make(method, path);
            }
        }

        return null;
    }

    private static RequestContext? ReadKestrel(ClrObject connection)
    {
        var path = TryString(connection, "<Path>k__BackingField") ?? TryString(connection, "_parsedPath");
        if (path is null)
        {
            return null;
        }

        var pathBase = TryString(connection, "<PathBase>k__BackingField") ?? "";
        string method;
        var custom = TryString(connection, "_methodText");
        var methodField = connection.Type!.GetFieldByName("<Method>k__BackingField");
        if (methodField is not null)
        {
            var value = methodField.Read<byte>(connection.Address, false);
            method = value < KestrelMethods.Length ? KestrelMethods[value] : custom ?? "?";
        }
        else
        {
            method = custom ?? "?";
        }

        return Make(method, pathBase + path);
    }

    private static RequestContext Make(string method, string path)
    {
        var route = NormalizePath(path);
        return new RequestContext(method, path, $"{method} {route}");
    }

    /// <summary>Replaces id-like segments (digits, GUIDs, long tokens) with <c>{id}</c>.</summary>
    public static string NormalizePath(string path)
    {
        var q = path.IndexOf('?');
        if (q >= 0)
        {
            path = path[..q];
        }

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            // "A-88200", "1234", GUIDs → {id}; short version-ish segments like "v2" stay.
            if (s.Length > 0 && ((IdLike().IsMatch(s) && (s.Length >= 3 || s.All(char.IsDigit))) || s.Length > 24))
            {
                segments[i] = "{id}";
            }
        }

        return string.Join('/', segments);
    }

    private static string? TryString(ClrObject obj, string field)
    {
        try
        {
            var f = obj.Type?.GetFieldByName(field);
            return f is { ElementType: ClrElementType.String } ? f.ReadString(obj.Address, false) : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"\d")]
    private static partial Regex IdLike();
}
