# 凌溪工具箱 (LingXi Toolbox)

Windows 桌面工具箱壳 + 模块化工具集。iOS 26 Liquid Glass 视觉语言 × .NET 8/WPF，从第一性原理出发：**壳只做平台服务，模块只做业务**。

> 完整设计见 [docs/开发文档.md](docs/开发文档.md)（17 章 + 5 附录，M0–M4 路线图）。

## 模块

| 模块 | Id | 说明 |
|---|---|---|
| 凌溪·音频 | `lx.audioswitch` | 音频输出设备快切。移植 QuickAudioSwitch 的纯 COM 互操作内核（`IPolicyConfig` 三角色切换），支持全局热键 Ctrl+Alt+A 循环切换、托盘直切、旧版 `devices.json` 一次性迁移 |
| 凌溪·监控 | `lx.monitor` | 机器监控仪表盘。LX Hub 本地收数端 100% 兼容 [servermonitor](https://github.com/qsbb/servermonitor) agent 协议（`POST /servermonitor/report` + `X-SM-Token`），可选托管官方 agent / 转发 Yunzai / 阈值告警 |

## 结构

```
src/
  LX.Module.Sdk    # 零依赖插件契约（ILxToolModule / ILxModuleContext）
  LX.Core          # 平台服务：设置 / 日志 / 托盘 / 热键 / 通知 / 自启 / 更新 / 单实例
  LX.Ui            # 设计令牌 + 玻璃控件（LxGlassCard / LxStatusPill / 主题管理）
  LX.Audio.Core    # 音频领域内核（纯 COM，net8.0）
  LX.AudioSwitch   # 音频模块
  LX.Monitor.Core  # 监控领域内核：快照契约 / LxHub / 存储 / 告警 / agent 托管（net8.0）
  LX.Monitor       # 监控模块
  LX.App           # 壳（WPF-UI FluentWindow + Mica，组合根）
tests/
  LX.Monitor.Core.Tests   # Linux 可跑：契约 / Hub / 告警 / token
  LX.Core.Tests           # Windows CI 跑：设置存取
```

依赖方向是硬约束：`模块 → SDK`，SDK 永不反向引用；WPF-UI 仅在 `LX.App` 引用；模块 UI 只消费 `LX.Ui` 的设计令牌。

## 构建

### Windows（运行目标）

```powershell
dotnet build -c Release          # 需 .NET 8 SDK + Windows
dotnet run --project src/LX.App
```

### Linux（仅编译验证）

```bash
dotnet build -c Release   # Directory.Build.props 已启用 EnableWindowsTargeting
```

无 libicu 的精简环境需要（所有 dotnet 调用）：

```bash
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
```

## 测试

```bash
dotnet test tests/LX.Monitor.Core.Tests   # 任何平台
dotnet test                               # 全部（Windows）
```

覆盖：真实 agent v0.1.13 dry-run 快照的契约解析、Hub 收数/别名路径/token 拒绝/404/健康检查/转发/跨域预检拒绝、在线-离线翻转、告警冷却、token 规格、设置 roundtrip 与损坏回退。

## 接入 servermonitor agent

1. 打开凌溪·监控页，复制「LX Hub 收数端」地址与 Token；
2. agent 侧配置 `--report-url http://127.0.0.1:2536/servermonitor/report` 与同一 Token（或在 `settings.json` 的 `lx.monitor` 段开启托管：`agentEnabled` + `agentScriptPath`/`agentExePath`）；
3. 可选转发：`forwardEnabled: true` + `forwardUrl` 指向 Yunzai 完整 report URL。

## 安全模型（vibe-coding-security 契约摘要）

- Hub 只绑定 `http://127.0.0.1`，默认不对局域网开放；
- Token 必配（`sm_` + 32hex）；空 token 集合仅用于本地测试；
- 请求体 1MB 上限；脏 JSON 返回 400，收数端永不崩溃；
- 浏览器跨域 POST：`application/json` 预检被 404 拒绝，`text/plain` 简单请求被 token 挡住；
- agent 子进程用环境变量传参（不走 shell），路径仅来自本机用户配置；
- 残余风险（接受）：token 明文存于 `%LocalAppData%\LingXi\settings.json`（与官方 agent 同级）、命名管道激活可被本机任意进程触发（仅弹出主窗口）。

## 路线图

- [x] M0 壳 + 模块 SDK
- [x] M1 凌溪·音频（QAS 内核移植）
- [x] M2 凌溪·监控（LX Hub + 告警 + agent 托管）
- [ ] M3 设置页 + 打包分发（Velopack）
- [ ] M4 生态化（模块市场 / 脚本模块）

## 许可

本仓库代码自研；重用资产：QuickAudioSwitch（COM 互操作内核）、[WPF-UI](https://github.com/lepoco/wpfui)（Fluent 控件）、[H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)、[NHotkey](https://github.com/thomaslevesque/NHotkey)、[servermonitor](https://github.com/qsbb/servermonitor)（协议与 agent）。
