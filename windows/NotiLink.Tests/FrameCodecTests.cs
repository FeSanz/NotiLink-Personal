using System.Text;
using NotiLink.Core.Protocol;
using Xunit;

namespace NotiLink.Tests;

public class FrameCodecTests
{
    [Fact]
    public void EncodeJson_RoundTripsThroughDecodeHeader()
    {
        var json = "{\"v\":1,\"type\":\"heartbeat\",\"id\":\"x\",\"ts\":0,\"payload\":{}}";
        var frame = FrameCodec.EncodeJson(json);

        var header = frame.AsSpan(0, FrameCodec.HeaderLength).ToArray();
        var (length, type) = FrameCodec.DecodeHeader(header);

        Assert.Equal(FrameCodec.TypeJson, type);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), length);

        var payload = Encoding.UTF8.GetString(frame, FrameCodec.HeaderLength, length);
        Assert.Equal(json, payload);
    }

    [Fact]
    public void EncodeBinary_ProducesCorrectLengthAndType()
    {
        byte[] data = [1, 2, 3, 4, 5];
        var frame = FrameCodec.EncodeBinary(data);

        var (length, type) = FrameCodec.DecodeHeader(frame.AsSpan(0, FrameCodec.HeaderLength));

        Assert.Equal(FrameCodec.TypeBinary, type);
        Assert.Equal(data.Length, length);
        Assert.Equal(data, frame[FrameCodec.HeaderLength..]);
    }

    [Fact]
    public void DecodeHeader_ThrowsOnWrongLength()
    {
        Assert.Throws<ArgumentException>(() => FrameCodec.DecodeHeader(new byte[4]));
    }
}
