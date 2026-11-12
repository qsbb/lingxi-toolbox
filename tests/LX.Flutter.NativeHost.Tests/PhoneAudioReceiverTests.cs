using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LingXi.Flutter.NativeHost;
using Xunit;

namespace LX.Flutter.NativeHost.Tests;

public sealed class PhoneAudioReceiverTests
{
    private const string Token = "test-pairing-token-123456";

    [Fact]
    public async Task Receiver_reports_waiting_as_disconnected_then_receives_pcm_and_disconnects()
    {
        var sink = new FakeAudioOutput();
        using var receiver = CreateReceiver(sink);
        var started = State(receiver.Start());
        var port = started.GetProperty("port").GetInt32();
        Assert.True(started.GetProperty("running").GetBoolean());
        Assert.False(started.GetProperty("connected").GetBoolean());
        Assert.Equal("等待手机连接", started.GetProperty("connection").GetString());

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
        await SendText(socket, new
        {
            type = "hello",
            protocol = "lx-pcm-v1",
            token = Token,
            device_id = "android-test",
            device_name = "AVD Test",
            sample_rate = 40000,
        });

        using var ready = JsonDocument.Parse(await ReceiveText(socket));
        Assert.Equal("ready", ready.RootElement.GetProperty("type").GetString());
        Assert.True(State(receiver.State()).GetProperty("connected").GetBoolean());

        var frame = new byte[8000 * sizeof(float)]; // exactly 200 ms at 40 kHz mono float32
        for (var i = 0; i < frame.Length; i += sizeof(float))
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(i, sizeof(float)), BitConverter.SingleToInt32Bits(0.25f));
        await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);

        var written = await sink.Written.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(40000, written.SampleRate);
        Assert.Equal(8000, written.Samples.Length);
        Assert.All(written.Samples, sample => Assert.Equal(0.25f, sample));
        Assert.Equal(9600 * 4, written.Pcm16Stereo.Length);
        Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(written.Pcm16Stereo));
        Assert.Equal(1, State(receiver.State()).GetProperty("frames").GetInt64());
        Assert.Equal(1, sink.OpenCount);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await WaitUntil(() => !State(receiver.State()).GetProperty("connected").GetBoolean());
        var stopped = State(receiver.Stop());
        Assert.False(stopped.GetProperty("running").GetBoolean());
        Assert.False(stopped.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public void Receiver_stop_is_idempotent_and_allows_a_later_restart()
    {
        using var receiver = CreateReceiver(new FakeAudioOutput());
        Assert.True(State(receiver.Start()).GetProperty("running").GetBoolean());
        Assert.False(State(receiver.Stop()).GetProperty("running").GetBoolean());
        Assert.False(State(receiver.Stop()).GetProperty("running").GetBoolean());
        Assert.True(State(receiver.Start()).GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task Receiver_rejects_invalid_pairing_token_before_opening_audio_sink()
    {
        var sink = new FakeAudioOutput();
        using var receiver = CreateReceiver(sink);
        var port = State(receiver.Start()).GetProperty("port").GetInt32();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
        await SendText(socket, new
        {
            type = "hello",
            protocol = "lx-pcm-v1",
            token = "wrong-token",
            device_id = "android-test",
            device_name = "AVD Test",
            sample_rate = 40000,
        });

        using var rejected = JsonDocument.Parse(await ReceiveText(socket));
        Assert.Equal("error", rejected.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, sink.WriteCount);
        Assert.Equal(0, sink.OpenCount);
        Assert.False(State(receiver.State()).GetProperty("connected").GetBoolean());
        receiver.Stop();
    }

    private static PhoneAudioReceiver CreateReceiver(FakeAudioOutput sink) => new(
        Token,
        0,
        IPAddress.Loopback,
        enforceWindows: false,
        renderDevices: () => [new WaveDevice(7, "LingXi Virtual Microphone Render")],
        captureDeviceNames: () => ["LingXi Virtual Microphone Capture"],
        outputFactory: (_, _) =>
        {
            sink.MarkOpened();
            return sink;
        });

    private static JsonElement State(object value) => JsonSerializer.SerializeToElement(value);

    private static Task SendText(ClientWebSocket socket, object message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReceiveText(ClientWebSocket socket)
    {
        var bytes = new byte[4096];
        using var result = new MemoryStream();
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(bytes, CancellationToken.None);
            result.Write(bytes, 0, received.Count);
        } while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(result.ToArray());
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("receiver did not disconnect in time");
            await Task.Delay(10);
        }
    }

    private sealed class FakeAudioOutput : IPhoneAudioOutput
    {
        internal TaskCompletionSource<(float[] Samples, int SampleRate, byte[] Pcm16Stereo)> Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int WriteCount => _writeCount;
        internal int OpenCount => _openCount;
        private int _writeCount;
        private int _openCount;
        internal void MarkOpened() => Interlocked.Increment(ref _openCount);

        public void Write(float[] input, int inputRate)
        {
            Interlocked.Increment(ref _writeCount);
            Written.TrySetResult((input, inputRate, PhonePcmEncoder.Encode(input, inputRate)));
        }

        public void Dispose() { }
    }
}

