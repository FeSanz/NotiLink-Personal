using System.Buffers.Binary;
using System.Text;

namespace NotiLink.Core.Protocol;

/// <summary>
/// Encodes/decodes the framing layer from docs/PROTOCOL.md:
/// [4-byte big-endian length][1-byte type][payload]. Shared by both the
/// server's read loop and, eventually, its send path for PC-&gt;phone messages.
/// </summary>
public static class FrameCodec
{
    public const int HeaderLength = 5;
    public const byte TypeJson = 0x01;
    public const byte TypeBinary = 0x02;

    public static byte[] EncodeJson(string json) => Encode(TypeJson, Encoding.UTF8.GetBytes(json));

    public static byte[] EncodeBinary(byte[] data) => Encode(TypeBinary, data);

    private static byte[] Encode(byte type, byte[] payload)
    {
        var frame = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), payload.Length);
        frame[4] = type;
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static (int Length, byte Type) DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderLength)
            throw new ArgumentException($"Header must be exactly {HeaderLength} bytes.", nameof(header));

        int length = BinaryPrimitives.ReadInt32BigEndian(header[..4]);
        byte type = header[4];
        return (length, type);
    }
}
