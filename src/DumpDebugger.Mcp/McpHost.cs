using System.Net;
using System.Security.Cryptography;
using System.Text;
using DumpDebugger.Core.Contracts;
using DumpDebugger.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace DumpDebugger.Mcp;

/// <summary>
/// Runs the MCP server inside the app when the user turns it on: Streamable HTTP at
/// <c>http://127.0.0.1:{port}/mcp</c>. It listens on loopback only, requires the bearer token on
/// every request, and refuses requests a web page could have made (an Origin header, or a Host
/// that isn't this loopback address), so neither another machine nor a browser tab can reach
/// the dump. Turning it off closes the dumps agents opened.
/// </summary>
public sealed class McpHost : IMcpControl, IAsyncDisposable
{
    public const string EndpointPath = "/mcp";

    private readonly McpSettingsStore _store;
    private readonly DumpRegistry _registry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpSettings _settings;
    private WebApplication? _app;
    private string? _error;

    public McpHost(Func<DumpSession?> appDump, McpSettingsStore? store = null)
    {
        _store = store ?? new McpSettingsStore();
        _settings = _store.Load();
        _registry = new DumpRegistry(appDump, () => _settings.AllowDacDownload);
    }

    public DumpRegistry Registry => _registry;

    public McpStatus Status
    {
        get
        {
            var s = _settings;
            return new McpStatus(true, s.Enabled, _app is not null, s.Port, $"http://127.0.0.1:{s.Port}{EndpointPath}", s.Token, s.AllowDacDownload, _error);
        }
    }

    /// <summary>Called once at launch: starts the server if the user left it on.</summary>
    public async Task<McpStatus> StartIfEnabledAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_settings.Enabled)
            {
                await StartCoreAsync().ConfigureAwait(false);
            }

            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<McpStatus> ConfigureAsync(McpSettingsParams p, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (p.Port is { } port && !McpSettingsStore.ValidPort(port))
            {
                _error = $"Port {port} isn't usable. Pick one between 1024 and 65535.";
                return Status;
            }

            var next = _settings with
            {
                Enabled = p.Enabled ?? _settings.Enabled,
                Port = p.Port ?? _settings.Port,
                AllowDacDownload = p.AllowDacDownload ?? _settings.AllowDacDownload,
            };
            var restart = next.Port != _settings.Port || next.Enabled != (_app is not null);
            _settings = next;
            _store.Save(next);

            if (restart)
            {
                await StopCoreAsync(closeDumps: !next.Enabled).ConfigureAwait(false);
                if (next.Enabled)
                {
                    await StartCoreAsync().ConfigureAwait(false);
                }
            }

            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The token is checked per request against the current settings, so rotating it needs no restart.</summary>
    public async Task<McpStatus> RegenerateTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _settings = _settings with { Token = McpSettingsStore.NewToken() };
            _store.Save(_settings);
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        _error = null;
        var port = _settings.Port;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(_registry);
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "dump-debugger",
                    Title = "Dump Debugger",
                    Version = typeof(McpHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                };
                options.ServerInstructions = DumpTools.Instructions;
            })
            .WithHttpTransport()
            .WithTools<DumpTools>();

        var app = builder.Build();
        app.Use(GuardAsync);
        app.MapMcp(EndpointPath);
        try
        {
            await app.StartAsync().ConfigureAwait(false);
            _app = app;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            _error = ex is IOException
                ? $"Port {port} is already in use by another program. Choose a different port."
                : $"The MCP server could not start: {ex.Message}";
        }
    }

    private async Task StopCoreAsync(bool closeDumps)
    {
        _error = null;
        if (_app is { } app)
        {
            _app = null;
            try
            {
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await app.StopAsync(patience.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }

            await app.DisposeAsync().ConfigureAwait(false);
        }

        if (closeDumps)
        {
            await _registry.CloseAllAsync().ConfigureAwait(false);
        }
    }

    private async Task GuardAsync(HttpContext context, RequestDelegate next)
    {
        var settings = _settings;
        var request = context.Request;

        // No browser is a legitimate client: a page that finds the port (DNS rebinding included) is turned away here.
        var host = request.Host.Value;
        if (request.Headers.ContainsKey("Origin")
            || (host != $"127.0.0.1:{settings.Port}" && host != $"localhost:{settings.Port}"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Dump Debugger's MCP server only accepts local, non-browser clients.").ConfigureAwait(false);
            return;
        }

        var header = request.Headers.Authorization.ToString();
        const string Scheme = "Bearer ";
        var presented = header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) ? header[Scheme.Length..].Trim() : "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(settings.Token)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsync("Missing or wrong bearer token. Copy the current one from the MCP panel in Dump Debugger.").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(closeDumps: true).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
