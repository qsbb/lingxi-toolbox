# RVC 按需服务控制（2026-10-07 实测）

Mac 不设置 RunAtLoad/KeepAlive；工具箱点击启动时才运行 RVC。停止后 launchd 仅保留注册，不留下监听控制服务。

## Mac 一次性辅助脚本

将本仓 `tools/rvc-server-control.py` 安装为 `~/.local/bin/rvc-server-control`（权限 700）。它只接受 `state/start/stop`，固定操作 `gui/<uid>/dev.lingxi.rvcServer`，每次 SSH 调用结束立即退出。RVC token 仍由已有启动包装器从用户私有文件读取；此脚本不读取或打印 token。

## Windows 本机配置（不要放仓库）

在 `%LOCALAPPDATA%/LingXi/rvc-ssh.json` 配置：

```json
{"Host":"<Mac LAN host>","User":"<Mac user>","IdentityFile":"<absolute local private key path>"}
```

- 使用现有 Windows OpenSSH 客户端；公钥预先加入 Mac，主机指纹经核对后预先登记在本机 known_hosts。
- 使用 `BatchMode=yes`、`StrictHostKeyChecking=yes` 和 `IdentitiesOnly=yes`，不接受密码提示或自动信任新主机。
- 密钥和实际配置不提交、不打包；原生请求只接受固定 start/stop，不接受任意远程命令。
- 控制返回 `controlMode: ssh`；未配置/断线时返回中文原因，HTTP 状态区独立保留。

## Windows OpenSSH 管道坑

实际 .NET WinExe + CreateNoWindow + 重定向管道测试：远端已经返回 JSON，但等进程退出/ReadToEnd 会卡住。接收一次性脚本的完整 JSON 行作为回执，随后清理本机 SSH 进程，可在 NativeBridge 的 15 秒门限内完成。必须验证状态翻转，不能只凭 exitCode=0 向界面报告成功。

## 回归

- `dotnet test tests/LX.Flutter.NativeHost.Tests/LX.Flutter.NativeHost.Tests.csproj`
- 真实 Windows NativeHost 的 state/stop/start/stop 路径通过；Mac 本地 NativeHost 启停通过。
- Flutter 生产 Controller 的双端角色卡创建/编辑/激活/恢复/删除、推理启停通过；临时测试卡清理后恢复原卡。
