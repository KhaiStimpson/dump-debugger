using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contoso.Scenarios;

/// <summary>A real Kestrel app on a loopback port, so dumps carry genuine HttpContext objects.</summary>
public static class WebHost
{
    public static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<WebApplication> mapEndpoints)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        mapEndpoints(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address), Timeout = Timeout.InfiniteTimeSpan };
        return (app, client);
    }

    /// <summary>Starts a request without waiting for it (it may never complete).</summary>
    public static void Fire(HttpClient client, HttpMethod method, string path) =>
        _ = client.SendAsync(new HttpRequestMessage(method, path)).ContinueWith(_ => { }, TaskScheduler.Default);
}
