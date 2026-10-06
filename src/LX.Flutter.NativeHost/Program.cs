using System.Text;
using System.Text.Json;
using LingXi.Host.Protocol;
using LingXi.Audio;
using LingXi.Monitor.Core;

namespace LingXi.Flutter.NativeHost;

/// <summary>
/// Flutter 与 Windows 原生能力之间的 JSON Lines 协议宿主。
/// stdout 只承载协议，诊断信息走 stderr；每行请求最多 256 KiB，响应最多由调用方限制。
/// </summary>
internal static class Program
{
    private static IAudioEndpointService? _audio;
    private static IAudioLoopbackService? _loopback;
    private static SystemMetricsCollector? _metrics;
    private static SystemProxyService? _proxy;
    private static LxHub? _hub;
    private static SnapshotStore? _store;
    private static readonly object Gate = new();

    private static async Task Main()
    {
        HostLog.Info("NativeHost started (pid " + Environment.ProcessId + ")");
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

    private static Task<Response> HandleAsync(Request request)
    {
        return request.Method switch
        {
            "ping" => Task.FromResult(Response.Success(request.Id, new
            {
                platform = "windows",
                version = "1.0",
                protocol = 1,
                capabilities = new[]
                {
                    "audio.list", "audio.default", "audio.setDefault",
                    // 音频监听（设备级捕获 + 转发）：WASAPI 环回实现
                    "audio.tapCapabilities", "audio.tapStart", "audio.tapStop", "audio.tapState",
                    "metrics.snapshot",
                    "hub.start", "hub.stop", "hub.listMachines", "hub.getMachine",
                    "net.proxyState", "net.repairProxy",
                    "llm.state", "llm.switch", "llm.detect", "llm.log",
                    // RVC 服务进程控制：通过 SSH 启停 Mac 上的 launchd 任务
                    "rvc.state", "rvc.switch",
                },
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
            "net.proxyState" => ProxyState(request),
            "net.repairProxy" => RepairProxy(request),
            "rvc.state" => Task.FromResult(RvcState(request)),
            "rvc.switch" => Task.FromResult(RvcSwitch(request)),
            "llm.state" => Task.FromResult(LlmState(request)),
            "llm.switch" => Task.FromResult(LlmSwitch(request)),
            "llm.detect" => Task.FromResult(LlmDetect(request)),
            "llm.log" => Task.FromResult(LlmLog(request)),
            _ => Task.FromResult(Response.Failure(request.Id, "unknown_method", "Unknown method")),
        };
    }

    private static Response AudioList(Request request)
    {
        try
        {
            _audio ??= AudioEndpointServiceFactory.Create();
            var output = _audio.GetDevices(DataFlow.Render).Select(ToAudio).ToList();
            var input = _audio.GetDevices(DataFlow.Capture).Select(ToAudio).ToList();
            return Response.Success(request.Id, new
            {
                output,
                input,
                outputDefault = _audio.GetDefaultId(DataFlow.Render),
                inputDefault = _audio.GetDefaultId(DataFlow.Capture),
            });
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "audio_unavailable", JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response AudioDefault(Request request)
    {
        var flow = GetFlow(request.Parameters);
        try
        {
            _audio ??= AudioEndpointServiceFactory.Create();
            return Response.Success(request.Id, new { id = _audio.GetDefaultId(flow) });
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "audio_unavailable", JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response AudioSetDefault(Request request)
    {
        try
        {
            var id = Required(request.Parameters, "id", 4096);
            _audio ??= AudioEndpointServiceFactory.Create();
            _audio.SetDefault(id);
            return Response.Success(request.Id, new { id });
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "audio_set_failed", JsonLineProtocol.PublicError(ex));
        }
    }

    // ------------------------------------------------------------------ 音频监听（设备级捕获 + 转发）

    private static IAudioLoopbackService? Loopback() =>
        _loopback ??= AudioLoopbackServiceFactory.Create();

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

            // targets 兼容数组与逗号分隔字符串（JsonElement 需单独判类型）
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
            Loopback()?.Stop(ReadStringParameter(request.Parameters, "sessionId"));
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

    private static Response MetricsSnapshot(Request request)
    {
        try
        {
            _metrics ??= new SystemMetricsCollector();
            _metrics.MachineName = Parameters.Optional(request.Parameters, "name")?.Trim() is { Length: > 0 } name
                ? name[..Math.Min(name.Length, 128)]
                : Environment.MachineName;
            var snapshot = _metrics.Collect();
            return snapshot is null
                ? Response.Failure(request.Id, "metrics_empty", "No usable metric was collected")
                : Response.Success(request.Id, snapshot);
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "metrics_failed", JsonLineProtocol.PublicError(ex));
        }
    }

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

                var offlineTimeout = Math.Clamp(Parameters.OptionalInt(request.Parameters, "offlineTimeoutSec") ?? 30, 5, 86400);
                var options = new HubOptions
                {
                    Port = port,
                    BindLan = bindLan,
                    // NativeHost never starts an unauthenticated Hub.
                    Tokens = new HashSet<string>(StringComparer.Ordinal) { token },
                    OfflineTimeout = TimeSpan.FromSeconds(offlineTimeout),
                };
                _store = new SnapshotStore();
                _store.SetOfflineTimeout(options.OfflineTimeout);
                HostLog.Info($"hub.start port={port} bindLan={bindLan}");
                _hub = new LxHub(options, _store);
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

    private static async Task<Response> ProxyState(Request request)
    {
        try
        {
            _proxy ??= new SystemProxyService();
            return Response.Success(request.Id, await _proxy.ReadAsync());
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "proxy_diagnose_failed", JsonLineProtocol.PublicError(ex));
        }
    }

    private static async Task<Response> RepairProxy(Request request)
    {
        try
        {
            _proxy ??= new SystemProxyService();
            var resetWinHttp = Parameters.OptionalBool(request.Parameters, "resetWinhttp") ?? false;
            return Response.Success(request.Id, await _proxy.RepairAsync(resetWinHttp));
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "proxy_repair_failed", JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response LlmState(Request request)
    {
        try
        {
            return Response.Success(request.Id, LlmService.State());
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "llm_state_failed", JsonLineProtocol.PublicError(ex));
        }
    }

    /// <summary>RVC 服务状态（远程问 Mac，只读）。</summary>
    private static Response RvcState(Request request)
    {
        try
        {
            return Response.Success(request.Id, RvcService.State(
                Parameters.Optional(request.Parameters, "host"),
                Parameters.Optional(request.Parameters, "user")));
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, JsonLineProtocol.ErrorCode(ex), JsonLineProtocol.PublicError(ex));
        }
    }

    /// <summary>RVC 服务启停（SSH 到 Mac 执行 launchctl）。</summary>
    private static Response RvcSwitch(Request request)
    {
        try
        {
            var action = Required(request.Parameters, "action", 16).Trim().ToLowerInvariant();
            return Response.Success(request.Id, RvcService.Switch(
                action,
                Parameters.Optional(request.Parameters, "host"),
                Parameters.Optional(request.Parameters, "user")));
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, JsonLineProtocol.ErrorCode(ex), JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response LlmSwitch(Request request)
    {
        try
        {
            var action = Required(request.Parameters, "action", 16)
                .Trim().ToLowerInvariant();
            var task = Parameters.Optional(request.Parameters, "task")?.Trim();
            return Response.Success(request.Id, LlmService.Execute(action, task));
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, JsonLineProtocol.ErrorCode(ex), JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response LlmDetect(Request request)
    {
        try
        {
            return Response.Success(request.Id, LlmService.Detect());
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "llm_detect_failed", JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response LlmLog(Request request)
    {
        try
        {
            return Response.Success(request.Id, LlmService.Log(Parameters.OptionalInt(request.Parameters, "maxLines")));
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "llm_log_failed", JsonLineProtocol.PublicError(ex));
        }
    }

    private static Response HubGetMachine(Request request)
    {
        try
        {
            var name = Required(request.Parameters, "name", 128);
            var store = _store;
            var entry = store?.Get(name);
            if (entry is null) return Response.Failure(request.Id, "machine_not_found", "Machine not found");
            return Response.Success(request.Id, new
            {
                name,
                online = store!.IsOnline(name, DateTimeOffset.Now),
                receivedAt = entry.ReceivedAt,
                snapshot = entry.Snapshot,
            });
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "invalid_parameters", JsonLineProtocol.PublicError(ex));
        }
    }

    private static AudioDto ToAudio(AudioEndpoint endpoint) => new(
        endpoint.Id,
        endpoint.Name,
        endpoint.State.ToString().ToLowerInvariant());

    private static DataFlow GetFlow(IReadOnlyDictionary<string, object?> parameters) =>
        string.Equals(Parameters.Optional(parameters, "flow"), "capture", StringComparison.OrdinalIgnoreCase)
            ? DataFlow.Capture
            : DataFlow.Render;

    private static string Required(IReadOnlyDictionary<string, object?> parameters, string key, int maxLength) =>
        Parameters.Optional(parameters, key) is { Length: > 0 } value && value.Length <= maxLength
            ? value
            : throw new ArgumentException($"Invalid parameter: {key}");

    private static void Shutdown()
    {
        HostLog.Info("NativeHost shutting down");
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

internal sealed record AudioDto(string Id, string Name, string State);

