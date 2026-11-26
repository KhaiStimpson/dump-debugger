using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Contoso.Scenarios;

/// <summary>
/// Just enough of a SQL Server (the TDS protocol) on a loopback port for SqlClient to log in and
/// run a query, so connection-pool scenarios get genuine <c>SqlConnection</c>s and pools without
/// a database. It runs in the DumpGen driver, not the victim, so it isn't in the dump.
/// Encryption is declined, any login is accepted, and every query returns one row with one
/// <c>int</c> column.
/// </summary>
public sealed class FakeSqlServer
{
    private const byte PreLogin = 0x12;
    private const byte Login7 = 0x10;
    private const byte SqlBatch = 0x01;
    private const byte Rpc = 0x03;
    private const byte TabularResult = 0x04;

    /// <summary>Environment variable the driver uses to tell a victim where the server listens.</summary>
    public const string PortVariable = "CONTOSO_SQL_PORT";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static FakeSqlServer Start()
    {
        var server = new FakeSqlServer();
        server._listener.Start();
        _ = Task.Run(server.AcceptLoopAsync);
        return server;
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync();
            client.NoDelay = true;
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private static async Task ServeAsync(TcpClient client)
    {
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            while (await ReadMessageAsync(stream) is { } message)
            {
                var reply = message.Type switch
                {
                    PreLogin => PreLoginResponse(),
                    Login7 => LoginResponse(),
                    SqlBatch => OneRowResponse(rpc: false),
                    Rpc => OneRowResponse(rpc: true),
                    _ => DoneOnly(),
                };
                await WritePacketAsync(stream, reply);
            }
        }
        catch (IOException)
        {
            // Client went away.
        }
    }

    private static async Task<(byte Type, byte[] Body)?> ReadMessageAsync(Stream stream)
    {
        var body = new MemoryStream();
        var header = new byte[8];
        byte type;
        while (true)
        {
            if (!await ReadExactlyAsync(stream, header))
            {
                return null;
            }

            type = header[0];
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            var payload = new byte[length - 8];
            if (!await ReadExactlyAsync(stream, payload))
            {
                return null;
            }

            body.Write(payload);
            if ((header[1] & 0x01) != 0)
            {
                break;
            }
        }

        return (type, body.ToArray());
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private static async Task WritePacketAsync(Stream stream, byte[] body)
    {
        var packet = new byte[body.Length + 8];
        packet[0] = TabularResult;
        packet[1] = 0x01; // end of message
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[6] = 1;
        body.CopyTo(packet, 8);
        await stream.WriteAsync(packet);
        await stream.FlushAsync();
    }

    private static byte[] PreLoginResponse()
    {
        // Options: VERSION, ENCRYPTION (not supported), INSTOPT, THREADID, MARS, terminator.
        (byte Token, byte[] Data)[] options =
        [
            (0x00, [16, 0, 0x10, 0x00, 0, 0]),
            (0x01, [0x02]),
            (0x02, [0x00]),
            (0x03, []),
            (0x04, [0x00]),
        ];
        var headerLength = options.Length * 5 + 1;
        var ms = new MemoryStream();
        var offset = headerLength;
        foreach (var (token, data) in options)
        {
            ms.WriteByte(token);
            WriteUInt16BE(ms, offset);
            WriteUInt16BE(ms, data.Length);
            offset += data.Length;
        }

        ms.WriteByte(0xFF);
        foreach (var (_, data) in options)
        {
            ms.Write(data);
        }

        return ms.ToArray();
    }

    private static byte[] LoginResponse()
    {
        var ms = new MemoryStream();
        EnvChange(ms, 1, "Orders", "master");
        EnvChange(ms, 4, "8000", "4096");

        // Collation (ENVCHANGE type 7, B_VARBYTE values): Latin1_General_CI_AS.
        ms.WriteByte(0xE3);
        WriteUInt16LE(ms, 1 + 1 + 5 + 1);
        ms.WriteByte(7);
        ms.WriteByte(5);
        ms.Write([0x09, 0x04, 0xD0, 0x00, 0x34]);
        ms.WriteByte(0);

        // LOGINACK: interface, TDS 7.4, program name, version.
        var name = Encoding.Unicode.GetBytes("Microsoft SQL Server");
        ms.WriteByte(0xAD);
        WriteUInt16LE(ms, 1 + 4 + 1 + name.Length + 4);
        ms.WriteByte(1);
        ms.Write([0x74, 0x00, 0x00, 0x04]);
        ms.WriteByte((byte)(name.Length / 2));
        ms.Write(name);
        ms.Write([16, 0, 0x10, 0x00]);

        // FEATUREEXTACK with no features, then DONE.
        ms.Write([0xAE, 0xFF]);
        Done(ms, 0x0000, 0, 0);
        return ms.ToArray();
    }

    /// <summary>A parameterized command arrives as an RPC (<c>sp_executesql</c>), which ends with a return status and DONEPROC instead of DONE.</summary>
    private static byte[] OneRowResponse(bool rpc)
    {
        var ms = new MemoryStream();

        // COLMETADATA: one nullable int column with no name.
        ms.WriteByte(0x81);
        WriteUInt16LE(ms, 1);
        ms.Write([0, 0, 0, 0]); // user type
        WriteUInt16LE(ms, 0x0001); // flags: nullable
        ms.Write([0x26, 0x04]); // INTN(4)
        ms.WriteByte(0); // column name

        // ROW: 1.
        ms.WriteByte(0xD1);
        ms.Write([0x04, 0x01, 0x00, 0x00, 0x00]);

        if (!rpc)
        {
            Done(ms, 0x0010, 0xC1, 1);
            return ms.ToArray();
        }

        Done(ms, 0x0010, 0xC1, 1, token: 0xFF); // DONEINPROC
        ms.Write([0x79, 0, 0, 0, 0]); // RETURNSTATUS 0
        Done(ms, 0x0000, 0xE0, 0, token: 0xFE); // DONEPROC
        return ms.ToArray();
    }

    private static byte[] DoneOnly()
    {
        var ms = new MemoryStream();
        Done(ms, 0, 0, 0);
        return ms.ToArray();
    }

    private static void EnvChange(MemoryStream ms, byte type, string newValue, string oldValue)
    {
        var n = Encoding.Unicode.GetBytes(newValue);
        var o = Encoding.Unicode.GetBytes(oldValue);
        ms.WriteByte(0xE3);
        WriteUInt16LE(ms, 1 + 1 + n.Length + 1 + o.Length);
        ms.WriteByte(type);
        ms.WriteByte((byte)newValue.Length);
        ms.Write(n);
        ms.WriteByte((byte)oldValue.Length);
        ms.Write(o);
    }

    private static void Done(MemoryStream ms, ushort status, ushort command, long rows, byte token = 0xFD)
    {
        ms.WriteByte(token);
        WriteUInt16LE(ms, status);
        WriteUInt16LE(ms, command);
        Span<byte> count = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(count, rows);
        ms.Write(count);
    }

    private static void WriteUInt16BE(MemoryStream ms, int value)
    {
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)value);
    }

    private static void WriteUInt16LE(MemoryStream ms, int value)
    {
        ms.WriteByte((byte)value);
        ms.WriteByte((byte)(value >> 8));
    }
}
