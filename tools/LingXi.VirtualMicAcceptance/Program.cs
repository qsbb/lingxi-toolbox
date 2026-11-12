using System.Runtime.InteropServices;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This acceptance test requires Windows.");
    return 2;
}

const string endpointName = "LingXi Virtual Microphone";
var render = WaveOutDevice.FindUnique(endpointName);
var capture = WaveInDevice.FindUnique(endpointName);
if (render is null || capture is null)
{
    Console.Error.WriteLine($"FAIL: expected exactly one render and capture endpoint named '{endpointName}'.");
    Console.Error.WriteLine($"Render matches: {render?.Name ?? "missing or ambiguous"}");
    Console.Error.WriteLine($"Capture matches: {capture?.Name ?? "missing or ambiguous"}");
    return 1;
}

Console.WriteLine($"Render:  [{render.Id}] {render.Name}");
Console.WriteLine($"Capture: [{capture.Id}] {capture.Name}");
Console.WriteLine("Playing a 997 Hz test tone only to the dedicated LingXi virtual render endpoint.");

try
{
    var result = LoopbackProbe.Run(render.Id, capture.Id, seconds: 3);
    Console.WriteLine($"Captured {result.Samples} samples; RMS={result.Rms:F4}; peak={result.Peak:F4}; 997Hz={result.ToneRms:F4}");
    if (result.Samples == 0 || result.Rms < 0.01 || result.ToneRms < 0.005)
    {
        Console.Error.WriteLine("FAIL: the virtual capture endpoint did not receive the rendered test tone.");
        return 1;
    }

    Console.WriteLine("PASS: render-to-capture PCM loopback is active.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex.Message}");
    return 1;
}

internal sealed record AudioDevice(int Id, string Name);
internal sealed record ProbeResult(int Samples, double Rms, double Peak, double ToneRms);

internal static class WaveOutDevice
{
    internal static AudioDevice? FindUnique(string token)
    {
        var found = new List<AudioDevice>();
        var count = Native.waveOutGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            if (Native.waveOutGetDevCaps((IntPtr)i, out var caps, (uint)Marshal.SizeOf<Native.WaveOutCaps>()) == 0)
            {
                var name = caps.Name.TrimEnd('\0');
                if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) found.Add(new((int)i, name));
            }
        }
        return found.Count == 1 ? found[0] : null;
    }
}

internal static class WaveInDevice
{
    internal static AudioDevice? FindUnique(string token)
    {
        var found = new List<AudioDevice>();
        var count = Native.waveInGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            if (Native.waveInGetDevCaps((IntPtr)i, out var caps, (uint)Marshal.SizeOf<Native.WaveInCaps>()) == 0)
            {
                var name = caps.Name.TrimEnd('\0');
                if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) found.Add(new((int)i, name));
            }
        }
        return found.Count == 1 ? found[0] : null;
    }
}

internal static class LoopbackProbe
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int Bits = 16;
    private const int WhdrDone = 0x00000001;
    private const uint CallbackFunction = 0x00030000;
    private const uint WimData = 0x03C0;
    private const double TestFrequency = 997;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WaveInCallback(IntPtr handle, uint message, IntPtr instance, IntPtr header, IntPtr reserved);

    internal static ProbeResult Run(int renderId, int captureId, int seconds)
    {
        var byteCount = SampleRate * Channels * (Bits / 8) * seconds;
        var captureBytes = Marshal.AllocHGlobal(byteCount);
        var renderBytes = Marshal.AllocHGlobal(byteCount);
        var captureHeaderPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.WaveHeader>());
        var renderHeaderPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Native.WaveHeader>());
        using var captured = new ManualResetEventSlim();
        IntPtr input = IntPtr.Zero;
        IntPtr output = IntPtr.Zero;
        var inputCallback = new WaveInCallback((handle, message, instance, header, reserved) =>
        {
            if (message == WimData) captured.Set();
        });
        try
        {
            FillTone(renderBytes, byteCount);
            var format = new Native.WaveFormat
            {
                FormatTag = 1,
                Channels = Channels,
                SamplesPerSec = SampleRate,
                AvgBytesPerSec = SampleRate * Channels * (Bits / 8),
                BlockAlign = Channels * (Bits / 8),
                BitsPerSample = Bits,
            };

            Check(Native.waveInOpen(out input, (uint)captureId, ref format,
                Marshal.GetFunctionPointerForDelegate(inputCallback), IntPtr.Zero, CallbackFunction), "open virtual microphone capture endpoint");
            Check(Native.waveOutOpen(out output, (uint)renderId, ref format, IntPtr.Zero, IntPtr.Zero, 0), "open virtual microphone render endpoint");

            var captureHeader = new Native.WaveHeader { Data = captureBytes, BufferLength = (uint)byteCount };
            var renderHeader = new Native.WaveHeader { Data = renderBytes, BufferLength = (uint)byteCount };
            Marshal.StructureToPtr(captureHeader, captureHeaderPtr, false);
            Marshal.StructureToPtr(renderHeader, renderHeaderPtr, false);
            Check(Native.waveInPrepareHeader(input, captureHeaderPtr, (uint)Marshal.SizeOf<Native.WaveHeader>()), "prepare capture buffer");
            Check(Native.waveOutPrepareHeader(output, renderHeaderPtr, (uint)Marshal.SizeOf<Native.WaveHeader>()), "prepare render buffer");
            Check(Native.waveInAddBuffer(input, captureHeaderPtr, (uint)Marshal.SizeOf<Native.WaveHeader>()), "queue capture buffer");
            Check(Native.waveInStart(input), "start capture");
            Thread.Sleep(150);
            Check(Native.waveOutWrite(output, renderHeaderPtr, (uint)Marshal.SizeOf<Native.WaveHeader>()), "play test tone");

            if (!captured.Wait(TimeSpan.FromSeconds(seconds + 3))) throw new TimeoutException("capture buffer was not completed");
            var doneCapture = Marshal.PtrToStructure<Native.WaveHeader>(captureHeaderPtr);
            if ((doneCapture.Flags & WhdrDone) == 0) throw new IOException("capture callback arrived before buffer completion");
            var recorded = Math.Min((int)doneCapture.BytesRecorded, byteCount);
            return Analyze(captureBytes, recorded);
        }
        finally
        {
            if (input != IntPtr.Zero) { Native.waveInStop(input); Native.waveInReset(input); }
            if (output != IntPtr.Zero) { Native.waveOutReset(output); }
            if (input != IntPtr.Zero) { try { Native.waveInUnprepareHeader(input, captureHeaderPtr, (uint)Marshal.SizeOf<Native.WaveHeader>()); } catch { } Native.waveInClose(input); }
            if (output != IntPtr.Zero) { try { Native.waveOutUnprepareHeader(output, renderHeaderPtr, (uint)Marshal.SizeOf<Native.WaveHeader>()); } catch { } Native.waveOutClose(output); }
            GC.KeepAlive(inputCallback);
            Marshal.FreeHGlobal(renderHeaderPtr);
            Marshal.FreeHGlobal(captureHeaderPtr);
            Marshal.FreeHGlobal(renderBytes);
            Marshal.FreeHGlobal(captureBytes);
        }
    }

    private static void FillTone(IntPtr buffer, int byteCount)
    {
        var data = new byte[byteCount];
        var frames = byteCount / (Channels * 2);
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * TestFrequency * frame / SampleRate) * short.MaxValue * 0.25);
            var offset = frame * Channels * 2;
            data[offset] = (byte)sample;
            data[offset + 1] = (byte)(sample >> 8);
            data[offset + 2] = data[offset];
            data[offset + 3] = data[offset + 1];
        }
        Marshal.Copy(data, 0, buffer, byteCount);
    }

    private static ProbeResult Analyze(IntPtr buffer, int byteCount)
    {
        var bytes = new byte[byteCount - byteCount % 4];
        Marshal.Copy(buffer, bytes, 0, bytes.Length);
        var frames = bytes.Length / 4;
        if (frames == 0) return new(0, 0, 0, 0);
        var samples = new double[frames];
        double sumSq = 0, peak = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var left = (short)(bytes[frame * 4] | bytes[frame * 4 + 1] << 8) / 32768.0;
            var right = (short)(bytes[frame * 4 + 2] | bytes[frame * 4 + 3] << 8) / 32768.0;
            var value = (left + right) * 0.5;
            samples[frame] = value;
            sumSq += value * value;
            peak = Math.Max(peak, Math.Abs(value));
        }
        var rms = Math.Sqrt(sumSq / frames);
        var toneRms = GoertzelRms(samples, TestFrequency);
        return new(frames, rms, peak, toneRms);
    }

    private static double GoertzelRms(double[] samples, double frequency)
    {
        var omega = 2 * Math.PI * frequency / SampleRate;
        var coefficient = 2 * Math.Cos(omega);
        double s1 = 0, s2 = 0;
        foreach (var sample in samples)
        {
            var s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var power = s1 * s1 + s2 * s2 - coefficient * s1 * s2;
        return Math.Sqrt(Math.Max(0, power) * 2) / samples.Length;
    }

    private static void Check(int result, string operation)
    {
        if (result != 0) throw new IOException($"Could not {operation} (wave error {result})");
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WaveOutCaps { public ushort ManufacturerId, ProductId; public uint DriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; public uint Support; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WaveInCaps { public ushort ManufacturerId, ProductId; public uint DriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveFormat { public ushort FormatTag, Channels; public uint SamplesPerSec, AvgBytesPerSec; public ushort BlockAlign, BitsPerSample, ExtraSize; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHeader { public IntPtr Data; public uint BufferLength, BytesRecorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }

    [DllImport("winmm.dll")] internal static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] internal static extern int waveOutGetDevCaps(IntPtr id, out WaveOutCaps caps, uint size);
    [DllImport("winmm.dll")] internal static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] internal static extern int waveInGetDevCaps(IntPtr id, out WaveInCaps caps, uint size);
    [DllImport("winmm.dll")] internal static extern int waveInOpen(out IntPtr handle, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] internal static extern int waveOutOpen(out IntPtr handle, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] internal static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] internal static extern int waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] internal static extern int waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] internal static extern int waveInStop(IntPtr handle);
    [DllImport("winmm.dll")] internal static extern int waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] internal static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] internal static extern int waveInClose(IntPtr handle);
    [DllImport("winmm.dll")] internal static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] internal static extern int waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] internal static extern int waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] internal static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] internal static extern int waveOutClose(IntPtr handle);
}
