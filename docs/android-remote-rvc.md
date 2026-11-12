
## Windows PCM 回环验收工具

`/Users/lingxi/project/lingxitoolbox/lingxi-toolbox/tools/LingXi.VirtualMicAcceptance/` 是无第三方依赖的 Windows 验收程序，构建后运行 `LingXi.VirtualMicAcceptance.exe`：

1. 必须恰好枚举到一个 `LingXi Virtual Microphone` render 和一个 capture 端点，否则失败退出。
2. 只向该专用 render 端点播放 997 Hz 测试音，不回退到默认扬声器。
3. 同步从对应 capture 端点录制，并用 RMS / Goertzel 频点能量检查是否真实收到测试音；设备存在但 PCM 未回环会判失败。

该工具本身不安装驱动、不更改系统启动设置，也不请求管理员权限。当前已在 macOS 上完成 `net8.0-windows/win-x64` 交叉编译（0 warning / 0 error），但必须到装有驱动的 Windows 测试机运行，才能形成硬件验收证据。

## NativeHost 音频接收协议测试（2026-10-08）

`tests/LX.Flutter.NativeHost.Tests/PhoneAudioReceiverTests.cs` 使用本机 loopback WebSocket 和 fake 音频 sink，不需要 Windows、虚拟驱动或真实 RVC：验证“等待手机”不误报 connected、`lx-pcm-v1` 配对成功、200 ms float32 帧被接收/转换并计数、无效配对码在打开音频 sink 前被拒绝，以及正常 WebSocket close handshake。测试覆盖协议层，不替代 WDK 驱动和真实 PCM 端点验收。
