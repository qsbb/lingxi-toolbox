# 凌溪虚拟麦克风驱动（开发中）

本目录从 Microsoft Windows Driver Samples 的 SysVAD/TabletAudioSample 派生，源快照 commit 记录在 `third_party/SYSVAD_COMMIT`，许可证副本在 `third_party/LICENSE.MS-PL`。

## 本地改动

- `TabletAudioSample/minipairs.h` 只安装一个 render miniport（原 Speaker）和一个 capture miniport（原 MicIn），不注册其余示例端点。
- `ComponentizedAudioSample.inx` 把两个 endpoint 显示名改为 `LingXi Virtual Microphone (Render)` 和 `LingXi Virtual Microphone`。
- render/capture 的音频契约固定为 48 kHz、2 声道、PCM16（4 bytes/frame）。`EndpointsCommon/minwavertstream.cpp` 在 render stream 消费 WaveRT DMA 区域时写入 500ms 有界 PCM ring；ring 按连续区段批量拷贝并在必要时处理一次回绕，避免 DPC 路径逐 4 字节循环，capture 欠载时输出静音，过载时丢弃最旧帧以限制延迟。 render stream 停止时会清空 ring 指针，避免下一次会话读到上次残留 PCM。
- 禁用示例 offload 与 loopback pin 的实例，以及 speaker endpoint 的 offload 能力；该项目不启用本机监听路径。

## 构建

在安装了 Visual Studio 2022 C++ 工作负载、Windows Driver Kit (WDK) 和 SDK 的 Windows 构建机执行：

```powershell
msbuild .\drivers\LingXiVirtualMic\third_party\sysvad\sysvad.sln /m /p:Configuration=Release /p:Platform=x64
```

包产物位置由 WDK/项目配置决定，构建后先用 `InfVerif` / `SignTool verify` 检查，再按单独的测试机流程安装。**本开发机是 macOS；此驱动尚未在 WDK 下编译、签名、安装或运行验证。不得在用户日常 Windows 机器上开启 test-signing、调整 Secure Boot/BitLocker 或重启。**

安装并重启测试机后，先运行 `lingxi-toolbox/tools/LingXi.VirtualMicAcceptance` 的 PCM 回环验收，再接 Android → Mac RVC → Windows 的网络音频验收。只看设备管理器里出现端点不算通过。
