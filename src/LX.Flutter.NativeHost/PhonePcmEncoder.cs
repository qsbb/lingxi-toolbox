using System.Buffers.Binary;

namespace LingXi.Flutter.NativeHost;

/// <summary>Converts normalized mono float PCM into the virtual sink's PCM16 stereo format.</summary>
internal static class PhonePcmEncoder
{
    internal const int OutputRate = 48000;
    internal const int OutputChannels = 2;

    /// <summary>
    /// Decodes one 200 ms RVC server output frame (float32 little-endian mono).
    /// The upstream lx-pcm-v1 contract returns exactly one output frame per
    /// 200 ms input block; reject partial/coalesced frames instead of shifting
    /// all subsequent audio timing.
    /// </summary>
    internal static float[] DecodeFrame(ReadOnlySpan<byte> bytes, int sampleRate)
    {
        if (sampleRate is < 8000 or > 96000)
            throw new InvalidDataException("PCM 采样率无效");
        var expectedSamples = (int)Math.Round(sampleRate * 0.2, MidpointRounding.AwayFromZero);
        if (bytes.Length != expectedSamples * sizeof(float))
            throw new InvalidDataException("RVC 音频帧长度无效（应为 200 ms float32 单声道）");
        var samples = new float[expectedSamples];
        for (var i = 0; i < samples.Length; i++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i * sizeof(float), sizeof(float)));
            samples[i] = BitConverter.Int32BitsToSingle(bits);
        }
        return samples;
    }

    internal static byte[] Encode(float[] input, int inputRate)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (inputRate is < 8000 or > 96000 || input.Length > inputRate * 2)
            throw new InvalidDataException("PCM 帧长度或采样率无效");
        var outCount = Math.Max(1, (int)Math.Round(input.Length * (double)OutputRate / inputRate));
        var pcm = new byte[outCount * OutputChannels * sizeof(short)];
        for (var i = 0; i < outCount; i++)
        {
            var position = i * (double)inputRate / OutputRate;
            var left = Math.Min((int)position, Math.Max(0, input.Length - 1));
            var right = Math.Min(left + 1, Math.Max(0, input.Length - 1));
            var fraction = position - left;
            var sample = input.Length == 0
                ? 0f
                : input[left] + (input[right] - input[left]) * (float)fraction;
            if (!float.IsFinite(sample)) sample = 0;
            var pcm16 = (short)Math.Clamp(
                (int)Math.Round(Math.Clamp(sample, -1f, 1f) * 32767f),
                short.MinValue, short.MaxValue);
            var offset = i * OutputChannels * sizeof(short);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset, sizeof(short)), pcm16);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset + sizeof(short), sizeof(short)), pcm16);
        }
        return pcm;
    }
}
