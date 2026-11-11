using DumpDebugger.Analysis.Frames;
using DumpDebugger.Analysis.Threads;

namespace DumpDebugger.Tests;

public class TextHelpersTests
{
    [Theory]
    [InlineData("/api/orders/A-88200/notes", "/api/orders/{id}/notes")]
    [InlineData("/api/inventory/SKU-2001", "/api/inventory/{id}")]
    [InlineData("/api/v2/orders/42?x=1", "/api/v2/orders/{id}")]
    [InlineData("/health", "/health")]
    public void Request_paths_normalize_to_route_shapes(string path, string expected) =>
        Assert.Equal(expected, RequestContextReader.NormalizePath(path));

    [Theory]
    [InlineData("Contoso.Orders.InventoryCache", "InventoryCache")]
    [InlineData("System.Collections.Generic.Dictionary<System.String,Contoso.Orders.StockItem>", "Dictionary<string,StockItem>")]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpProtocol+<ProcessRequests>d__236<System.__Canon>", "HttpProtocol")]
    [InlineData("System.Byte[]", "byte[]")]
    public void Type_names_shorten_for_display(string full, string expected) =>
        Assert.Equal(expected, FrameClassifier.ShortTypeName(full));
}
