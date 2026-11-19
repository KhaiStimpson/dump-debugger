using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DumpDebugger.Core.Rpc;
using DumpDebugger.Session;

namespace DumpDebugger.Mcp;

/// <summary>The MCP server's persisted choices. Off until the user turns it on; the choice then sticks across launches.</summary>
public sealed record McpSettings(bool Enabled, int Port, bool AllowDacDownload, string Token)
{
    public const int DefaultPort = 47600;
}

/// <summary>
/// <c>mcp.json</c> under %LOCALAPPDATA%\DumpDebugger. The token is the only secret: it is stored
/// encrypted for the current Windows user (DPAPI) and stays the same across launches so client
/// configurations keep working until the user regenerates it.
/// </summary>
public sealed class McpSettingsStore(string? filePath = null)
{
    private readonly string _path = filePath ?? Path.Combine(AppPaths.Ensure(""), "mcp.json");

    public McpSettings Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), ContractJson.Options) is { } stored)
            {
                var token = Unprotect(stored.Token);
                var settings = new McpSettings(stored.Enabled, ValidPort(stored.Port) ? stored.Port : McpSettings.DefaultPort, stored.AllowDacDownload, token ?? NewToken());
                if (token is null)
                {
                    Save(settings);
                }

                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable settings: start again from the safe default (off).
        }

        var fresh = new McpSettings(false, McpSettings.DefaultPort, false, NewToken());
        Save(fresh);
        return fresh;
    }

    public void Save(McpSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(settings.Enabled, settings.Port, settings.AllowDacDownload, Protect(settings.Token)), ContractJson.Indented));
        File.Move(temp, _path, overwrite: true);
    }

    public static bool ValidPort(int port) => port is >= 1024 and <= 65535;

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Protect(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        return OperatingSystem.IsWindows()
            ? "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser))
            : "plain:" + Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? stored)
    {
        try
        {
            if (stored is null)
            {
                return null;
            }

            if (stored.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), null, DataProtectionScope.CurrentUser));
            }

            return stored.StartsWith("plain:", StringComparison.Ordinal) ? Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..])) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // copied from another user or machine: issue a new token
        }
    }

    private sealed record Stored(bool Enabled, int Port, bool AllowDacDownload, string? Token);
}
