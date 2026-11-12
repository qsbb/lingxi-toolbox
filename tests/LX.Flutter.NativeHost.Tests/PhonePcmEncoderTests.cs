using System.Buffers.Binary;
using LingXi.Flutter.NativeHost;
using Xunit;

namespace LX.Flutter.NativeHost.Tests;

public sealed class PhonePcmEncoderTests
{
    [Fact]
    public void DecodeFrameReadsA200MsFloat32LittleEndianMonoFrame()
    {
        var input = new byte[8000 * sizeof(float)];
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(0, 4), BitConverter.SingleToInt32Bits(-0.5f));
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(4, 4), BitConverter.SingleToInt32Bits(0.25f));

        var decoded = PhonePcmEncoder.DecodeFrame(input, 40000);

        Assert.Equal(8000, decoded.Length);
        Assert.Equal(-0.5f, decoded[0]);
        Assert.Equal(0.25f, decoded[1]);
    }

    [Theory]
    [InlineData(39999)]
    [InlineData(40001)]
    public void DecodeFrameRejectsPartialOrCoalescedRvcFrames(int sampleCount)
    {
        var input = new byte[sampleCount * sizeof(float)];
        Assert.Throws<InvalidDataException>(() => PhonePcmEncoder.DecodeFrame(input, 40000));
    }

    [Fact]
    public void EncodeResamplesAndDuplicatesMonoToStereoPcm16()
    {
        var output = PhonePcmEncoder.Encode(new[] { -1f, 0f, 1f }, 48000);
        Assert.Equal(3 * 2 * sizeof(short), output.Length);
        Assert.Equal((short)-32767, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(0, 2)));
        Assert.Equal((short)-32767, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(2, 2)));
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(4, 2)));
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(6, 2)));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(8, 2)));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(10, 2)));
    }
}
