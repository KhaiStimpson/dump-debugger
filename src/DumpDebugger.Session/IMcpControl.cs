using DumpDebugger.Core.Contracts;

namespace DumpDebugger.Session;

/// <summary>
/// The MCP server as the app's settings panel sees it: read its status, turn it on or off,
/// change its port, rotate its token. Implemented by DumpDebugger.Mcp; the bridge only routes.
/// </summary>
public interface IMcpControl
{
    McpStatus Status { get; }

    Task<McpStatus> ConfigureAsync(McpSettingsParams settings, CancellationToken ct);

    Task<McpStatus> RegenerateTokenAsync(CancellationToken ct);
}
