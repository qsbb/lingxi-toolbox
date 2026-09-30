using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LingXi.Audio;
using LingXi.Host.Protocol;
using LingXi.Monitor.Core;

namespace LingXi.Flutter.NativeHost.Mac;

/// <summary>
/// macOS 原生能力宿主：与 Windows 版共用 <see cref="JsonLineProtocol"/>（同一份报文上限与错误码），
/// 只在平台后端上分叉。stdout 只承载协议，诊断走 ~/Library/Logs/LingXi。
/// </summary>
internal static class Program
{
    /// <summary>本宿主支持的方法（ping 的 capabilities 用它，保证与实现同源）。</summary>
    private static readonly string[] Capabilities =
    [
        "audio.list", "audio.default", "audio.setDefault",
        "audio.tapCapabilities", "audio.tapStart", "audio.tapStop", "audio.tapState",
        "metrics.snapshot",
        "hub.start", "hub.stop", "hub.listMachines", "hub.getMachine",
        "net.proxyState", "net.repairProxy",
        "llm.state", "llm.switch", "llm.detect", "llm.log",
    ];

    private static MacAudioService? _audio;
    private static IAudioLoopbackService? _loopback;
    private static MacMetricsCollector? _metrics;
    private static LxHub? _hub;
    private static SnapshotStore? _store;
    private static readonly object Gate = new();

    private static async Task Main()
    {
        HostLog.Info("NativeHost(macOS) started (pid " + Environment.ProcessId + ")");
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, leaveOpen: false);
        await using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), leaveOpen: false)
        {
            AutoFlush = true,
        };

        while (true)
        {
            string? line;
            try
            {
                line = await JsonLineProtocol.ReadLineLimitedAsync(reader, JsonLineProtocol.MaxRequestLineLength);
            }
            catch (ProtocolException ex)
            {
                HostLog.Error($"protocol error: {ex.Code}");
                await JsonLineProtocol.WriteResponseAsync(writer, Response.Failure(null, ex.Code, "Invalid request"));
                continue;
            }

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            Request? request = null;
            Response response;
            try
            {
                request = JsonSerializer.Deserialize<Request>(line, JsonLineProtocol.Json)
                    ?? throw new ProtocolException("invalid_json", "request is null");
                JsonLineProtocol.Validate(request);
                response = await HandleAsync(request);
            }
            catch (Exception ex)
            {
                HostLog.Error($"request failed: {request?.Method ?? "(no method)"}", ex);
                response = Response.Failure(request?.Id, JsonLineProtocol.ErrorCode(ex), JsonLineProtocol.PublicError(ex));
            }

            await JsonLineProtocol.WriteResponseAsync(writer, response);
        }

        Shutdown();
    }

    private static Task<Response> HandleAsync(Request request) => request.Method switch
    {
        "ping" => Task.FromResult(Response.Success(request.Id, new
        {
            platform = "macos",
            version = "1.0",
            protocol = 1,
            capabilities = Capabilities,
        })),
        "audio.list" => Task.FromResult(AudioList(request)),
        "audio.default" => Task.FromResult(AudioDefault(request)),
        "audio.setDefault" => Task.FromResult(AudioSetDefault(request)),
        "audio.tapCapabilities" => Task.FromResult(AudioTapCapabilities(request)),
        "audio.tapStart" => Task.FromResult(AudioTapStart(request)),
        "audio.tapStop" => Task.FromResult(AudioTapStop(request)),
        "audio.tapState" => Task.FromResult(AudioTapState(request)),
        "metrics.snapshot" => Task.FromResult(MetricsSnapshot(request)),
        "hub.start" => Task.FromResult(HubStart(request)),
        "hub.stop" => Task.FromResult(HubStop(request)),
        "hub.listMachines" => Task.FromResult(HubListMachines(request)),
        "hub.getMachine" => Task.FromResult(HubGetMachine(request)),
        // llm.state 即使拿不到计划任务也要**成功返回**：前端靠 tasksAvailable=false
        // 把启停/切换区置灰并给出说明（"仅服务器本机可操作"），
        // 若整条调用失败则 _hostState 为 null，按钮只会灰着而不解释。
        "llm.state" => Task.FromResult(Response.Success(request.Id, new
        {
            processRunning = LocalLlamaServerRunning(),
            tasksAvailable = false, // schtasks/taskkill 是 Windows 专属，macOS 无本地可启停实例
            tasks = new Dictionary<string, string>(),
        })),
        // 其余 Windows 特有语义返回结构化 not_supported：
        // 前端据此降级而不是报"操作失败"。
        "net.proxyState" or "net.repairProxy" or "llm.switch" or "llm.detect" or "llm.log" =>
            Task.FromResult(NotSupported(request)),
        _ => Task.FromResult(Response.Failure(request.Id, "unknown_method", "Unknown method")),
    };

    /// <summary>上报到达的打点限频器（每 60s 最多记一条，避免刷爆日志）。</summary>
    private static class ReportTicker
    {
        private static readonly object Gate2 = new();
        private static DateTime _last = DateTime.MinValue;

        internal static void Tick()
        {
            lock (Gate2)
            {
                var now = DateTime.Now;
                if ((now - _last).TotalSeconds < 60) return;
                _last = now;
                HostLog.Info("hub: snapshot received");
            }
        }
    }

    /// <summary>本机是否有 llama-server 进程（macOS 版也可能在本地起服务）。</summary>
    private static bool LocalLlamaServerRunning()
    {
        try
        {
            return Process.GetProcessesByName("llama-server").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Windows 专属能力的显式降级（前端据此显示"该平台不支持"）。</summary>
    private static Response NotSupported(Request request) => Response.Failure(
        request.Id,
        "not_supported",
        $"macOS 版暂未提供该能力：{request.Method}");

    // ------------------------------------------------------------------ 音频

    private static Response AudioList(Request request)
    {
        try
        {
            _audio ??= new MacAudioService();
            return Response.Success(request.Id, new
            {
                output = _audio.ListOutputs(),
                input = _audio.ListInputs(),
                outputDefault = _audio.DefaultOutputId(),
                inputDefault = _audio.DefaultInputId(),
            });
        }
        catch (Exception ex)
        {
            HostLog.Error("audio.list failed", ex);
            return Response.Failure(request.Id, "audio_unavailable", JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response AudioDefault(Request request)
    {
        try
        {
            _audio ??= new MacAudioService();
            var capture = string.Equals(Parameters.Optional(request.Parameters, "flow"), "capture",
                StringComparison.OrdinalIgnoreCase);
            return Response.Success(request.Id, new
            {
                id = capture ? _audio.DefaultInputId() : _audio.DefaultOutputId(),
            });
        }
        catch (Exception ex)
        {
            HostLog.Error("audio.default failed", ex);
            return Response.Failure(request.Id, "audio_unavailable", JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response AudioSetDefault(Request request)
    {
        try
        {
            var id = Parameters.Optional(request.Parameters, "id") is { Length: > 0 and <= 4096 } value
                ? value
                : throw new ArgumentException("Invalid parameter: id");
            var capture = string.Equals(Parameters.Optional(request.Parameters, "flow"), "capture",
                StringComparison.OrdinalIgnoreCase);
            _audio ??= new MacAudioService();
            _audio.SetDefault(id, capture);
            return Response.Success(request.Id, new { id });
        }
        catch (Exception ex)
        {
            HostLog.Error("audio.setDefault failed", ex);
            return Response.Failure(request.Id, "audio_set_failed", JsonLineProtocol.PublicError(ex));
        }
    }

    // ------------------------------------------------------------------ 音频监听（设备级捕获 + 转发）

    private static IAudioLoopbackService? Loopback()
    {
        // 工厂在不支持的平台返回 null；这里缓存实例（会话状态挂在服务上）
        return _loopback ??= AudioLoopbackServiceFactory.Create();
    }

    private static Response AudioTapCapabilities(Request request)
    {
        var service = Loopback();
        if (service is null)
        {
            return Response.Success(request.Id, new
            {
                supported = false,
                reason = "当前平台不支持音频监听",
                maxTargets = 0,
                canCaptureDevice = false,
            });
        }
        var caps = service.GetCapabilities();
        return Response.Success(request.Id, new
        {
            supported = caps.Supported,
            reason = caps.Reason,
            maxTargets = caps.MaxTargets,
            canCaptureDevice = caps.CanCaptureDevice,
        });
    }

    /// <summary>读取字符串参数（兼容 JsonElement / 原生 string）。</summary>
    private static string? ReadStringParameter(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var raw)) return null;
        return raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement e => e.ToString(),
            _ => raw?.ToString(),
        };
    }

    private static Response AudioTapStart(Request request)
    {
        var service = Loopback();
        if (service is null)
            return Response.Failure(request.Id, "tap_unsupported", "当前平台不支持音频监听");

        try
        {
            var source = ReadStringParameter(request.Parameters, "sourceDeviceId")?.Trim();
            if (string.IsNullOrEmpty(source))
                throw new ArgumentException("sourceDeviceId is required");

            // targets 兼容数组与逗号分隔字符串两种形态（前端可能传任一）。
            // ⚠️ System.Text.Json 把 object? 反序列化成 JsonElement（struct，
            //    不实现 IEnumerable），所以必须按 JsonElement 判类型；
            //    直接判 IEnumerable 会拿到空列表（实测踩过）。
            var targets = new List<string>();
            if (request.Parameters.TryGetValue("targetDeviceIds", out var raw))
            {
                switch (raw)
                {
                    case JsonElement { ValueKind: JsonValueKind.Array } array:
                        foreach (var item in array.EnumerateArray())
                        {
                            var value = item.GetString()?.Trim();
                            if (!string.IsNullOrEmpty(value)) targets.Add(value);
                        }
                        break;
                    case JsonElement { ValueKind: JsonValueKind.String } single:
                        targets.AddRange((single.GetString() ?? string.Empty)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                    case string joined:
                        targets.AddRange(joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        break;
                }
            }
            if (targets.Count == 0)
                throw new ArgumentException("targetDeviceIds is required");

            var gain = double.TryParse(ReadStringParameter(request.Parameters, "gain"), out var g) ? g : 1.0;
            var session = service.Start(new AudioTapRequest(source, targets, gain));
            HostLog.Info($"audio.tapStart source={source} targets={targets.Count} sr={session.SampleRate} latency={session.LatencyMs:F1}ms");
            return Response.Success(request.Id, new
            {
                sessionId = session.SessionId,
                sourceDeviceId = session.SourceDeviceId,
                targetDeviceIds = session.TargetDeviceIds,
                sampleRate = session.SampleRate,
                channels = session.Channels,
                latencyMs = session.LatencyMs,
                latencyEstimated = session.LatencyEstimated,
            });
        }
        catch (AudioTapException ex)
        {
            HostLog.Error($"audio.tapStart failed: {ex.Code}");
            return Response.Failure(request.Id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            HostLog.Error("audio.tapStart failed", ex);
            return Response.Failure(request.Id, "tap_start_failed", JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response AudioTapStop(Request request)
    {
        try
        {
            Loopback()?.Stop(Parameters.Optional(request.Parameters, "sessionId"));
            HostLog.Info("audio.tapStop");
            return Response.Success(request.Id, new { stopped = true });
        }
        catch (Exception ex)
        {
            HostLog.Error("audio.tapStop failed", ex);
            return Response.Failure(request.Id, "tap_stop_failed", JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response AudioTapState(Request request)
    {
        var status = Loopback()?.GetStatus();
        if (status is null) return Response.Success(request.Id, new { active = false });
        return Response.Success(request.Id, new
        {
            active = status.Active,
            sessionId = status.SessionId,
            sourceDeviceId = status.SourceDeviceId,
            targetDeviceIds = status.TargetDeviceIds,
            peakLeft = status.PeakLeft,
            peakRight = status.PeakRight,
            droppedFrames = status.DroppedFrames,
            uptimeSeconds = status.UptimeSeconds,
        });
    }

    // ------------------------------------------------------------------ 指标

    private static Response MetricsSnapshot(Request request)
    {
        try
        {
            _metrics ??= new MacMetricsCollector();
            var name = Parameters.Optional(request.Parameters, "name")?.Trim() is { Length: > 0 } n
                ? n
                : Environment.MachineName;
            return Response.Success(request.Id, _metrics.Collect(name));
        }
        catch (Exception ex)
        {
            HostLog.Error("metrics.snapshot failed", ex);
            return Response.Failure(request.Id, "metrics_failed", JsonLineProtocol.PublicError(ex));
        }
    }

    // ------------------------------------------------------------------ Hub（跨平台，直接复用）

    private static Response HubStart(Request request)
    {
        lock (Gate)
        {
            var port = Math.Clamp(Parameters.OptionalInt(request.Parameters, "port") ?? 2536, 1024, 65535);
            var bindLan = Parameters.OptionalBool(request.Parameters, "bindLan") ?? true;
            try
            {
                _hub?.Dispose();
                var token = Parameters.Optional(request.Parameters, "token")?.Trim();
                if (string.IsNullOrWhiteSpace(token)) token = TokenGen.NewToken();
                if (token.Length > 512) throw new ProtocolException("invalid_token", "token is too long");

                var offlineTimeout = Math.Clamp(
                    Parameters.OptionalInt(request.Parameters, "offlineTimeoutSec") ?? 30, 5, 86400);
                var options = new HubOptions
                {
                    Port = port,
                    BindLan = bindLan,
                    // 宿主绝不启动无鉴权的 Hub。
                    Tokens = new HashSet<string>(StringComparer.Ordinal) { token },
                    OfflineTimeout = TimeSpan.FromSeconds(offlineTimeout),
                };
                _store = new SnapshotStore();
                _store.SetOfflineTimeout(options.OfflineTimeout);
                HostLog.Info($"hub.start port={port} bindLan={bindLan}");
                _hub = new LxHub(options, _store);
                // 可观测性：Hub 内部异常（绑定回退、端口重试、上报处理失败）直接落宿主日志；
                // 收到上报按 60s 限频打点 —— 否则"监控是不是在收数"只能靠猜。
                _hub.Log += message =>
                {
                    // Hub 的 Log 事件既有提示（"已启动"）也有故障（"被拒"/"失败"），
                    // 按内容分级，避免把正常信息记成 ERR 污染排障。
                    var text = "hub: " + message;
                    if (message.Contains("失败") || message.Contains("被拒"))
                        HostLog.Error(text);
                    else
                        HostLog.Info(text);
                };
                _hub.SnapshotReceived += _ => ReportTicker.Tick();
                _hub.Start();
                HostLog.Info($"hub.start ok port={_hub.Port} lanBound={_hub.IsLanBound}");
                return Response.Success(request.Id, new
                {
                    port = _hub.Port,
                    reportUrl = _hub.ReportUrl,
                    lanReportUrl = _hub.LanReportUrl,
                    lanBound = _hub.IsLanBound,
                    token,
                });
            }
            catch (Exception ex)
            {
                HostLog.Error($"hub.start failed port={port}", ex);
                return Response.Failure(request.Id, "hub_start_failed", JsonLineProtocol.PublicError(ex));
            }
        }
    }

    private static Response HubStop(Request request)
    {
        lock (Gate)
        {
            HostLog.Info(_hub is null ? "hub.stop (no hub running)" : $"hub.stop port={_hub.Port}");
            _hub?.Dispose();
            _hub = null;
            _store = null;
            return Response.Success(request.Id, new { stopped = true });
        }
    }

    private static Response HubListMachines(Request request)
    {
        var store = _store;
        if (store is null) return Response.Success(request.Id, new { machines = Array.Empty<object>() });

        var now = DateTimeOffset.Now;
        var machines = store.GetAll(64).Select(entry => new
        {
            name = entry.Snapshot.Name,
            online = store.IsOnline(entry.Snapshot.Name, now),
            receivedAt = entry.ReceivedAt,
            snapshot = entry.Snapshot,
        });
        return Response.Success(request.Id, new { machines, truncated = store.GetAll(65).Count > 64 });
    }

    private static Response HubGetMachine(Request request)
    {
        var store = _store;
        if (store is null) return Response.Failure(request.Id, "hub_not_running", "Hub is not running");
        var name = Parameters.Optional(request.Parameters, "name")?.Trim();
        if (string.IsNullOrEmpty(name)) return Response.Failure(request.Id, "invalid_parameters", "name is required");
        var entry = store.Get(name);
        if (entry is null) return Response.Failure(request.Id, "machine_not_found", "Machine not found");
        var now = DateTimeOffset.Now;
        return Response.Success(request.Id, new
        {
            name = entry.Snapshot.Name,
            online = store.IsOnline(entry.Snapshot.Name, now),
            receivedAt = entry.ReceivedAt,
            snapshot = entry.Snapshot,
        });
    }

    private static void Shutdown()
    {
        HostLog.Info("NativeHost(macOS) shutting down");
        lock (Gate)
        {
            _hub?.Dispose();
            _audio?.Dispose();
            _loopback?.Dispose();
            _hub = null;
            _audio = null;
            _loopback = null;
            _store = null;
        }
    }
}
