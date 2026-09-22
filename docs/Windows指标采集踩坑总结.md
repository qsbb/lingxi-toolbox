# Windows 指标采集踩坑总结（来自凌溪工具箱实机自检）

> 适用：任何在 Windows 上采集 CPU/内存/GPU/磁盘指标并上报的工具
> 来源：2026-09-21~22 在 192.168.5.55（Win11 专业工作站 / 9950X3D / RTX 4070 + AMD 核显）实机自检
> 所有结论均有实测数据与复现命令，可直接抄作业

---

## 一、GPU 显存：`AdapterRAM` 是 32 位字段，≥4GB 一定错

**现象**：RTX 4070（12GB）上报成 4GB。

**根源**：`Win32_VideoController.AdapterRAM` 是 `UInt32`，11.9GB = 12,878,610,432 字节
在 32 位空间里被截断成 `4293918720`（0xFFFFF000 量级）。

**实测**：
```
WMI  AdapterRAM = 4293918720   → 4 GB（错）
nvidia-smi     = 12282 MiB     → 12 GB（对）
```

**做法**：命中 `>= 4GiB - 1MiB` 一律视为不可信，宁可不显示也别报错的数。
```csharp
static bool IsBogusAdapterRam(double bytes) => bytes >= 4.0 * 1024 * 1024 * 1024 - 1024 * 1024;
```

---

## 二、GPU 动态指标：`nvidia-smi` 是唯一可靠来源（NVIDIA）

**现象**：占用率/温度/功耗全空，只有型号。

**根源**：`Win32_VideoController` 只给型号和上面那个错显存，**没有**占用率/温度/功耗；
必须调 `nvidia-smi`。

**关键细节（踩了才知道）**：

1. **不一定在 PATH**，但一定在 System32（驱动安装时放进去的）：
   ```csharp
   var exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
   if (!File.Exists(exe)) return;   // 非 NVIDIA 机器直接跳过
   ```
2. **查某一项时不要同时用 `-q`**，报错 `ERROR: Option -q is not recognized`：
   ```powershell
   nvidia-smi --query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total,power.draw --format=csv,noheader
   ```
3. **中文区域输出用逗号作小数点**：`35,03 W`。只按 InvariantCulture 解析会让功耗**恒为 null**（这个 bug 很隐蔽，因为其它字段是整数看不出来）。
   ```csharp
   static double? ParseNvidiaNumber(string raw) {
       var text = raw.Trim().TrimEnd('%').Trim();
       if (text is "" or "N/A" or "[N/A]") return null;
       // 取前导数字，逗号当小数点
       var end = 0;
       while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or ',' or '-' or '+')) end++;
       if (end == 0) return null;
       return double.TryParse(text[..end].Replace(',', '.'), NumberStyles.Float,
                              CultureInfo.InvariantCulture, out var v) ? Math.Round(v, 1) : null;
   }
   ```
4. `memory.*` 单位是 **MiB**，要 `/1024` 转 GB 再上报。
5. 拿不到的字段（被动散热无功耗、非 NVIDIA）**传 null**，不要反推或填 0 —— 服务端会把它当"没有这一列"隐藏。

---

## 三、GPU 占用率归属：别用 `phys_N`，多显卡会串

**现象**：核显与独显并存时，占用率对不上卡（`phys_0` 在两张卡上都出现）。

**计数器实例名格式**：
```
pid_35316_luid_0x00000000_0x00014FC6_phys_0_eng_7_engtype_VideoEncode
        ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^  ← LUID 才唯一标识一块适配器
```

**能用的映射链**：
`GPU Engine 计数器的 LUID` → 注册表 `HKLM\SYSTEM\CurrentControlSet\Control\Video\{GUID}\0000`
的 `HardwareInformation.AdapterLuidLowPart` + `HardwareInformation.AdapterString` → 与 WMI 显卡名模糊匹配。

**实测限制（重要）**：**Win11 上这组注册表值不一定存在**（本机就不存在），
所以映射失败是常态。此时正确做法是：
- **不要**用序号/最大值硬套到某张卡上（会张冠李戴）
- 给该卡打一个 `readable: false`，UI 显示"不可读"，如实告知

```csharp
static string? ExtractLuid(string instanceName) {
    var marker = "_luid_0x";
    var start = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (start < 0) return null;
    var rest = instanceName[(start + marker.Length)..];
    var underscore = rest.IndexOf('_');
    if (underscore < 0) return null;
    var high = rest[..underscore];
    rest = rest[(underscore + 1)..];
    if (!rest.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
    var end = rest.IndexOf('_', 2);
    var low = end < 0 ? rest : rest[..end];
    return $"0x{high.ToLowerInvariant()}_{low.ToLowerInvariant()}";
}
```

**关于 DXGI（试过，不建议）**：想用 `CreateDXGIFactory1` 直接拿 LUID→名称最干净，
但 .NET 8 下 `[Out] out object` 封送会抛 `InvalidOleVariantTypeException`；
且 `[ComImport]` **不会继承父接口的 vtable 槽位**——`EnumAdapters1` 实际在第 12 槽
（前面有 IUnknown×3 + IDXGIObject×3 + IsCurrent + EnumAdapters + MakeWindowAssociation
+ GetWindowAssociation + CreateSwapChain + CreateSoftwareAdapter）。
要正确声明全部父方法才行，成本高于收益，除非你本来就要枚举 DXGI 适配器。

---

## 四、虚拟显示适配器：一定要过滤

`Win32_VideoController` 会把串流/模拟器/AR 的虚拟适配器一并列出。本机实测 7 个条目里 5 个是假的：

```
GameViewer Virtual Display Adapter   ← 网易 GameViewer 串流
Zako Display Adapter                 ← 第三方面板
Virtual Desktop Monitor              ← VD
Meta Virtual Monitor                 ← Quest
MuMu Virtual Display Adapter         ← MuMu 模拟器
NVIDIA GeForce RTX 4070              ← 真
AMD Radeon(TM) Graphics              ← 真（核显）
```

判定：名称含 `Virtual / Virtio / VMware / VirtualBox / Hyper-V / Microsoft Basic /
GameViewer / MuMu / Meta Virtual / Zako / Sunshine / Parsec / DisplayLink / USB Display` 的丢弃；
但只要名字里带 `NVIDIA / AMD / Radeon / Intel / Arc` 就保留（避免误杀）。

---

## 五、内存"可用量"：别用 `AvailableKBytes`

**现象**：想上报"可立即使用的内存"，`Win32_OperatingSystem.AvailableKBytes` 读出来是 **0**。

**根源**：该属性在 Win10/11 上是废弃类里的，基本恒为 0（甚至不存在）。

**正确来源**（实测 47550 MB，与任务管理器一致）：
```csharp
using var s = new ManagementObjectSearcher(
    "SELECT AvailableMBytes FROM Win32_PerfFormattedData_PerfOS_Memory");
// 兜底：PerformanceCounter("Memory", "Available Bytes")
```

**语义提醒**：`available`（可立即用）≠ `total - used`（后者含 page cache）。
不要用后者反推前者，否则等于把旧统计包装成新字段——服务端会按 `total - available` 算内存压力，值错了会误导。

---

## 六、构建/发布链路：改共享库后发布产物不更新

**现象**：改了 `LX.Monitor.Core`（被 NativeHost 引用的共享库），`flutter build` 报成功，
但部署的 `NativeHost.exe` 是几天前的旧文件 —— 新字段一直"没生效"。

**根源**：CMake 的 `add_custom_command(DEPENDS ...)` 只监听 NativeHost 自己的 `.cs`，
共享库文件不在依赖里，所以 nativehost_publish 没被触发。

**修复**：把被引用项目的源码也纳入依赖：
```cmake
file(GLOB_RECURSE NATIVEHOST_SOURCES CONFIGURE_DEPENDS
  "${SRC}/LX.Flutter.NativeHost/*.cs"
  "${SRC}/LX.Monitor.Core/*.cs"      # ← 共享库也要
  "${SRC}/LX.Audio.Core/*.cs")
```

**自检动作**（每次部署后必做）：对比**产物时间戳**与源码时间戳，别信"构建成功"。
```powershell
(Get-Item "D:\Tools\凌溪工具箱\LingXi.Flutter.NativeHost.exe").LastWriteTime   # 应与源码修改时间同批次
```

---

## 七、上报链路自检清单（这次就是这么定位的）

1. **配置面**：上报目标的 `Enabled` 是否为 true？本机自报被禁用时 Hub 会一直是空的（0 台），
   而直报另一端可能仍然正常 —— 容易误判为"Hub 坏了"。
2. **数据面**：直接读一次 `metrics.snapshot`，对照系统真实值逐字段核对：
   ```
   采集到的: available=46.6 GB
   系统真实: FreePhysical=46.6 GB  → 一致
   ```
3. **服务端面**：手工 POST 一次真实快照，看返回码语义：
   - `200` = 已登记 token，正常
   - `202` = 待绑定（需在机器人侧执行绑定命令）
   - `401` = token 无效/格式不符；`422` = 结构错或空快照；`429` = 限速
4. **产物面**：确认部署的 exe 时间戳是新的（见第六节）。

### 顺带发现的 token 格式问题
协议规定 `sm_` + 32 位小写 hex。本机一条直报 token 是 `sm_114514`（9 字符）——
因为服务端"已登记 token"只做配置匹配、不校验格式，所以它**能用但埋在雷**：
一旦服务端收紧校验、或做迁移/重新登记，这条就会断。建议统一用规范格式重新登记。

---

## 八、可直接复用的结论（TL;DR）

| 指标 | 正确来源 | 坑 |
|---|---|---|
| GPU 型号 | `Win32_VideoController.Name` | 需过滤虚拟适配器 |
| GPU 显存 | `nvidia-smi memory.total` | `AdapterRAM` 32 位溢出，≥4GB 必错 |
| GPU 占用率/温度/功耗 | `nvidia-smi --query-gpu=...` | 在 System32 而非 PATH；中文区域逗号小数点 |
| GPU 多卡归属 | LUID（计数器实例名里） | 注册表/ DXGI 都可能拿不到 → 显示"不可读" |
| 内存"可用" | `PerfFormattedData_PerfOS_Memory.AvailableMBytes` | `AvailableKBytes` 恒 0；别用 total-used 反推 |
| 构建产物 | CMake DEPENDS 含共享库 | 否则构建成功但部署旧 exe |
