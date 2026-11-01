using System.Buffers.Binary;
using System.Text;

namespace PotatoDM.BrowserIpc.Tests;

public sealed class NativeMessageFramingTests
{
    [Fact]
    public async Task WriteAsync_WritesLittleEndianLengthThenUtf8Payload()
    {
        using var stream = new MemoryStream();
        const string json = "{\"suggestedFileName\":\"中文.bin\"}";

        await NativeMessageFraming.WriteAsync(stream, json);

        var bytes = stream.ToArray();
        var payload = Encoding.UTF8.GetBytes(json);

        Assert.Equal(payload.Length, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(payload, bytes[4..]);
    }

    [Fact]
    public async Task RoundTrip_PreservesNonAsciiText()
    {
        using var stream = new MemoryStream();
        const string json = "{\"suggestedFileName\":\"中文 文件.bin\"}";

        await NativeMessageFraming.WriteAsync(stream, json);
        stream.Position = 0;

        Assert.Equal(json, await NativeMessageFraming.ReadAsync(stream));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(NativeMessageFraming.MaxMessageBytes + 1)]
    public async Task ReadAsync_RejectsLengthsOutsideTheAllowedRange(int length)
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        stream.Write(header);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => NativeMessageFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenPayloadIsTruncated()
    {
        using var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        stream.Write(header);
        stream.Write(Encoding.UTF8.GetBytes("abc"));
        stream.Position = 0;

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => NativeMessageFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task WriteAsync_RejectsOversizedMessages()
    {
        using var stream = new MemoryStream();
        var json = new string('a', NativeMessageFraming.MaxMessageBytes + 1);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => NativeMessageFraming.WriteAsync(stream, json));
    }
}
