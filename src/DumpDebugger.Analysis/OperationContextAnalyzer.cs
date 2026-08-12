using DumpDebugger.Core.Dump;
using Microsoft.Diagnostics.Runtime;

namespace DumpDebugger.Analysis;

/// <summary>
/// Phase 5 (PLAN.md §4.1 "Operation context"). For each thread, first looks for an
/// HttpContext-shaped object among the thread's GC stack roots (works for both
/// System.Web.HttpContext on .NET Framework and DefaultHttpContext on ASP.NET Core — the
/// exact backing-field names for Request/Path/Method vary by runtime and servicing version,
/// so this searches by field-name substring rather than hardcoding one shape, same spirit as
/// the identifying-field heuristic in MemoryAnalyzer). Falls back to the deepest
/// non-framework frame on the thread's own captured stack when no HttpContext is found —
/// PLAN.md's "generic fallback".
/// </summary>
public static class OperationContextAnalyzer
{
    public static OperationContextInfo? Infer(ClrThread thread, ThreadInfo threadInfo)
    {
        // Computed once and reused for both branches below: it's also the best available
        // source-code location for an HttpContext-described thread, since "which HttpContext
        // field held the request" isn't itself a single line of source worth linking to.
        var appFrame = threadInfo.Frames.FirstOrDefault(f =>
            f.TypeName is not null && !FrameworkFrames.IsFrameworkType(f.TypeName));

        var httpDescription = TryDescribeHttpContext(thread);
        if (httpDescription is not null)
        {
            return new OperationContextInfo(threadInfo.OSThreadId, "HttpContext", httpDescription, appFrame?.Location);
        }

        return appFrame is null
            ? null
            : new OperationContextInfo(threadInfo.OSThreadId, "GenericFrame", $"{appFrame.TypeName}.{appFrame.MethodName}", appFrame.Location);
    }

    private static string? TryDescribeHttpContext(ClrThread thread)
    {
        try
        {
            foreach (var root in thread.EnumerateStackRoots())
            {
                var typeName = root.Object.Type?.Name;
                if (typeName is null || !typeName.Contains("HttpContext", StringComparison.Ordinal))
                {
                    continue;
                }

                var description = DescribeRequest(root.Object);
                if (description is not null)
                {
                    return description;
                }
            }
        }
        catch
        {
            // Best-effort: a corrupt or partially-unwound stack shouldn't break thread listing.
        }

        return null;
    }

    private static string? DescribeRequest(ClrObject httpContext)
    {
        if (httpContext.Type is null)
        {
            return null;
        }

        foreach (var field in httpContext.Type.Fields)
        {
            if (field.Name is null || !field.Name.Contains("request", StringComparison.OrdinalIgnoreCase)
                || field.ElementType != ClrElementType.Class)
            {
                continue;
            }

            var requestObj = field.ReadObject(httpContext.Address, false);
            if (requestObj.IsNull || requestObj.Type is null)
            {
                continue;
            }

            var method = FindStringFieldContaining(requestObj, "method");
            var path = FindStringFieldContaining(requestObj, "path") ?? FindStringFieldContaining(requestObj, "url");

            if (method is not null || path is not null)
            {
                return $"{method ?? "?"} {path ?? "?"}";
            }
        }

        return null;
    }

    private static string? FindStringFieldContaining(ClrObject obj, string nameHint)
    {
        if (obj.Type is null)
        {
            return null;
        }

        foreach (var field in obj.Type.Fields)
        {
            if (field.Name is null || !field.Name.Contains(nameHint, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (field.ElementType == ClrElementType.String)
                {
                    var value = field.ReadString(obj.Address, false);
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
                else if (field.ElementType is ClrElementType.Struct or ClrElementType.Class)
                {
                    // PathString and similar wrapper structs typically carry a single string field.
                    var nested = field.ElementType == ClrElementType.Struct
                        ? field.ReadStruct(obj.Address, false)
                        : (ClrValueType?)null;

                    if (nested is { Type: not null } nestedStruct)
                    {
                        var inner = nestedStruct.Type.Fields.FirstOrDefault(f => f.ElementType == ClrElementType.String);
                        if (inner is not null)
                        {
                            var value = inner.ReadString(nestedStruct.Address, false);
                            if (!string.IsNullOrEmpty(value))
                            {
                                return value;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Unreadable field; try the next candidate.
            }
        }

        return null;
    }
}
