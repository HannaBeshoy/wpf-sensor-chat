using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chat.Protocol;

/// <summary>
/// TCP is a byte stream with no message boundaries, so every message is sent as a
/// frame: a 4-byte big-endian length, then that many bytes of UTF-8 JSON. The reader
/// reads exactly the length it was told, however the bytes were split into segments,
/// and refuses frames larger than MaxFrame, so a client cannot make the server
/// allocate gigabytes by sending a huge length.
/// </summary>
public static class FrameCodec
{
    public const int MaxFrame = 1 << 20;   // 1 MiB: a 64 KiB file chunk in base64 fits easily

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Encode(Message m)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(m, Json);
        if (body.Length > MaxFrame) throw new ProtocolException($"message of {body.Length} bytes is larger than {MaxFrame}");
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    public static async Task WriteAsync(Stream s, Message m, CancellationToken ct = default)
    {
        byte[] frame = Encode(m);
        await s.WriteAsync(frame, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one message; null when the other side closed the connection cleanly between frames.</summary>
    public static async Task<Message?> ReadAsync(Stream s, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        int got = await ReadFullyAsync(s, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 4) throw new ProtocolException("connection closed inside a frame header");
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length <= 0 || length > MaxFrame) throw new ProtocolException($"frame length {length} out of range");
        byte[] body = new byte[length];
        if (await ReadFullyAsync(s, body, ct).ConfigureAwait(false) < length)
            throw new ProtocolException("connection closed inside a frame");
        try
        {
            return JsonSerializer.Deserialize<Message>(body, Json) ?? throw new ProtocolException("empty message");
        }
        catch (JsonException e)
        {
            throw new ProtocolException("malformed JSON: " + e.Message);
        }
    }

    private static async Task<int> ReadFullyAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

public sealed class ProtocolException(string message) : Exception(message);
