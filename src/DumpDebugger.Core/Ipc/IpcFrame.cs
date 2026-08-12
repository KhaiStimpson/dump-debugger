using System.Text;
using System.Text.Json;

namespace DumpDebugger.Core.Ipc;

/// <summary>
/// Reads/writes IpcEnvelope frames on a Stream as: 4-byte little-endian length prefix,
/// followed by that many bytes of UTF-8 JSON. Used symmetrically by the App (client) and
/// the Worker (server) ends of the named pipe.
/// </summary>
public static class IpcFrame
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task WriteAsync(Stream stream, IpcEnvelope envelope, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var lengthPrefix = BitConverter.GetBytes(bytes.Length);
        await stream.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<IpcEnvelope?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var lengthPrefix = new byte[4];
        if (!await ReadExactAsync(stream, lengthPrefix, ct).ConfigureAwait(false))
        {
            return null;
        }

        var length = BitConverter.ToInt32(lengthPrefix);
        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
        {
            return null;
        }

        var json = Encoding.UTF8.GetString(payload);
        return JsonSerializer.Deserialize<IpcEnvelope>(json, JsonOptions);
    }

    public static T? DeserializePayload<T>(IpcEnvelope envelope) =>
        JsonSerializer.Deserialize<T>(envelope.PayloadJson, JsonOptions);

    public static string SerializePayload<T>(T payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }
}
