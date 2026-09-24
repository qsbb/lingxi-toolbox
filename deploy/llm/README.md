# lx.llm 模型服务：计划任务与隐藏启动器

Flutter 侧「模型服务」页的启动/停止/切换，走 NativeHost 的 `llm.state` / `llm.switch`，
落到 Windows 计划任务 `llama_srv` / `llama_fn`。本目录存放这两条任务的**动作定义约定**
与伴随脚本，避免"机器上一改就没人知道原来长什么样"。

## 为什么需要 run-hidden-sync.vbs

`llama.cpp` 的启动脚本（`D:\AI\llamacpp\start-*.bat`）是**阻塞式**的：
它前台跑 `llama-server.exe` 直到进程退出。而计划任务原本直接调
`cmd /c D:\AI\llamacpp\start-llm.bat`，于是会弹出一个**空白的命令行黑框**——
因为 bat 把 stdout/stderr 全部重定向进了 `server.log`，控制台里什么都没有，
用户只看到一个黑框挂在那里，观感极差也容易被误当成"卡死"。

`run-hidden-sync.vbs` 用 `WScript.Shell.Run(cmd, 0, True)` 启动同样的 cmd：

- `0` = 窗口隐藏（不创建可见控制台）；
- `True` = **同步等待**——这是关键，VBS 会一直阻塞到 bat 结束，
  于是计划任务的状态仍是「正在运行」，NativeHost 的 `llm.state`
  （靠 `schtasks /query` 判 Running）语义不变。

⚠️ 不要改成 `start "" /min` 或异步 VBS：那样 cmd 会 detach，计划任务立刻回到
「就绪」，NativeHost 会误判为"启动失败"，整个启停链路失效。

## 任务动作（两条任务一致，只有 bat 名不同）

```xml
<Command>wscript.exe</Command>
<Arguments>//B "D:\AI\llamacpp\run-hidden-sync.vbs" "D:\AI\llamacpp\start-llm.bat"</Arguments>
```

`llama_fn` 把最后的 `start-llm.bat` 换成 `start-fn.bat`。

## 改任务定义的正确姿势

`schtasks` 导出的 XML 是 **UTF-16**；用 `[xml]` 对象模型改再 `Save()` 会静默失败
（属性丢失/格式不对）。实测可行的是**正则文本替换 + 按 Unicode 写回**：

```powershell
$f = "D:\AI\llamacpp\task-llama_srv.xml"
$txt = Get-Content -LiteralPath $f -Raw -Encoding Unicode
$new = '<Command>wscript.exe</Command><Arguments>//B "D:\AI\llamacpp\run-hidden-sync.vbs" "D:\AI\llamacpp\start-llm.bat"</Arguments>'
$txt2 = [regex]::Replace($txt, '(?s)<Command>.*?</Command>\s*<Arguments>.*?</Arguments>', $new)
Set-Content -LiteralPath $f -Value $txt2 -Encoding Unicode
cmd /c "schtasks /create /tn llama_srv /xml D:\AI\llamacpp\task-llama_srv.xml /f"
```

改完必须回读核对：

```powershell
[xml]$y = (cmd /c "schtasks /query /tn llama_srv /xml" | Out-String)
$y.Task.Actions.Exec.Command; $y.Task.Actions.Exec.Arguments
```

## 已验证无效的方案（别再试）

| 方案 | 结果 |
|---|---|
| `wscript //B` 跑**异步** VBS（`Run(cmd,0,False)`） | 任务立刻回「就绪」，NativeHost 判启动失败 |
| `powershell -WindowStyle Hidden -Command "& '<bat>'"` | 空转，bat 根本没执行 |
| `cmd /c start "" /min <bat>` | detach → 任务掉回「就绪」，同上 |

## 日志

两个 bat 都把输出 `>` 重定向到**同一个** `D:\AI\llamacpp\server.log`，
所以切换模型后旧模型的日志会被覆盖，只保留当前实例。NativeHost 的
`llm.log` 读的就是这个文件（按运行中 `llama-server.exe` 所在目录推导，
服务没跑时回退默认路径，便于看"启动失败"的现场）。
