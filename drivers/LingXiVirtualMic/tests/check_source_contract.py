from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SYSVAD = ROOT / "third_party" / "sysvad"

render_inx = (SYSVAD / "TabletAudioSample/ComponentizedAudioSample.inx").read_bytes().decode("utf-16")
ring_header = (SYSVAD / "VirtualMicPcmRing.h").read_text(encoding="utf-8")
ext_inx = (SYSVAD / "TabletAudioSample/ComponentizedAudioSampleExtension.inx").read_bytes().decode("utf-16")
minipairs = (SYSVAD / "TabletAudioSample/minipairs.h").read_text(encoding="utf-8")
ring_cpp = (SYSVAD / "EndpointsCommon/minwavertstream.cpp").read_text(encoding="utf-8")
mic_fmt = (SYSVAD / "TabletAudioSample/micinwavtable.h").read_text(encoding="utf-8")
speaker_fmt = (SYSVAD / "EndpointsCommon/speakerwavtable.h").read_text(encoding="utf-8")

assert "Root\\LingXiVirtualMic" in render_inx
assert "Root\\LingXiVirtualMic" in ext_inx
assert 'SYSVAD.WaveSpeaker.szPname="LingXi Virtual Microphone (Render)"' in render_inx
assert 'SYSVAD.WaveMicIn.szPname="LingXi Virtual Microphone"' in render_inx

render_array = re.search(r"g_RenderEndpoints\[\]\s*=\s*\{(.*?)\};", minipairs, re.S).group(1)
capture_array = re.search(r"g_CaptureEndpoints\[\]\s*=\s*\{(.*?)\};", minipairs, re.S).group(1)
assert re.findall(r"&\w+", render_array) == ["&SpeakerMiniports"]
assert re.findall(r"&\w+", capture_array) == ["&MicInMiniports"]

assert "MICIN_DEVICE_MAX_CHANNELS           2" in mic_fmt
assert "MICIN_MIN_SAMPLE_RATE                48000" in mic_fmt
assert "MICIN_MAX_SAMPLE_RATE                48000" in mic_fmt
assert re.search(r"WAVE_FORMAT_EXTENSIBLE,\s*2,\s*48000,\s*192000,\s*4,\s*16", mic_fmt)
assert "SPEAKER_HOST_MIN_SAMPLE_RATE                48000" in speaker_fmt
assert "SPEAKER_HOST_MAX_SAMPLE_RATE                48000" in speaker_fmt
assert "LINGXI_PCM_FRAME_BYTES = 4" in ring_cpp
assert "LINGXI_PCM_RING_BYTES = 48000 * 4 / 2" in ring_cpp
assert "LingXiPcmRingRead(m_pDmaBuffer + bufferOffset, runWrite)" in ring_cpp
assert "LingXiCopyRenderDmaToPcmRing(m_pDmaBuffer" in ring_cpp
ring_write = ring_cpp.split("VOID LingXiPcmRingWrite(", 1)[1].split("VOID LingXiPcmRingRead(", 1)[0]
ring_read = ring_cpp.split("VOID LingXiPcmRingRead(", 1)[1].split("//=============================================================================\n// CMiniportWaveRTStream", 1)[0]
assert "for (ULONG offset" not in ring_write and "for (ULONG offset" not in ring_read
assert ring_write.count("RtlCopyMemory(") >= 2 and ring_read.count("RtlCopyMemory(") >= 2
assert "Source += Bytes - usableBytes" in ring_write
assert "RtlZeroMemory(Destination + readBytes, Bytes - readBytes)" in ring_read
assert "void LingXiPcmRingReset();" in ring_header
reset = ring_cpp.split("VOID LingXiPcmRingReset()", 1)[1].split("VOID LingXiPcmRingWrite", 1)[0]
assert "g_LingXiPcmReadOffset = 0" in reset
assert "g_LingXiPcmWriteOffset = 0" in reset
set_state = ring_cpp.split("NTSTATUS CMiniportWaveRTStream::SetState", 1)[1].split("NTSTATUS CMiniportWaveRTStream::SetFormat", 1)[0]
assert "if (!m_bCapture)" in set_state and "LingXiPcmRingReset();" in set_state

write_bytes = ring_cpp.split("VOID CMiniportWaveRTStream::WriteBytes", 1)[1].split("VOID CMiniportWaveRTStream::ReadBytes", 1)[0]
assert "m_ToneGenerator.GenerateSine" not in write_bytes
print("PASS: one render/capture pair, fixed stereo48 PCM16, DMA-to-ring path, silence-on-underrun path")
print("NOTE: static source contract only; WDK build and real device loopback remain required")

adapter = (SYSVAD / "adapter.cpp").read_text(encoding="utf-8-sig")
driver_entry = adapter.split("DriverEntry\n(", 1)[1].split("// DriverEntry", 1)[0]
assert '#include "VirtualMicPcmRing.h"' in adapter
assert driver_entry.index("LingXiPcmRingInitialize();") < driver_entry.index("WdfDriverCreate(")
update_position = ring_cpp.split("VOID CMiniportWaveRTStream::UpdatePosition", 1)[1].split("VOID CMiniportWaveRTStream::WriteBytes", 1)[0]
assert "if (m_bCapture)" in update_position
assert update_position.index("if (m_bCapture)") < update_position.index("LingXiCopyRenderDmaToPcmRing(")
assert "LingXiPcmRingRead(m_pDmaBuffer + bufferOffset, runWrite)" in write_bytes
print("PASS: PCM ring initialized before WDF startup; capture consumes ring and render produces it")
print("PASS: queued PCM is flushed when the virtual render stream stops")
