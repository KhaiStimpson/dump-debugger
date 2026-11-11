using System.Buffers.Binary;
using System.Text.Json;

namespace DumpDebugger.Core.Rpc;

/// <summary>
/// Pipe framing: a 4-byte little-endian length prefix followed by that many bytes of UTF-8
/// JSON (one <see cref="RpcMessage"/>). The WebView2 bridge carries the same JSON without the
/// prefix, since postMessage already delimits messages.
/// </summary>
public static class RpcFrame
{
    private const int MaxFrameBytes = 256 * 1024 * 1024;

    public static byte[] Encode(RpcMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options);
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        return frame;
    }

    public static async Task<RpcMessage?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var prefix = new byte[4];
        if (!await ReadExactAsync(stream, prefix, ct).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException($"RPC frame length {length} is out of range.");
        }

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
        {
            return null;
        }

        return JsonSerializer.Deserialize<RpcMessage>(payload, ContractJson.Options);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
