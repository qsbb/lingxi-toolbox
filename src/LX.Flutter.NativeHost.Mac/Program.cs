using System.Text;
using System.Text.Json;
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
        "audio.list", "audio.default", "audio.setDefault", "metrics.snapshot",
        "hub.start", "hub.stop", "hub.listMachines", "hub.getMachine",
        "net.proxyState", "net.repairProxy",
        "llm.state", "llm.switch", "llm.detect", "llm.log",
    ];

    private static MacAudioService? _audio;
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
        "metrics.snapshot" => Task.FromResult(MetricsSnapshot(request)),
        "hub.start" => Task.FromResult(HubStart(request)),
        "hub.stop" => Task.FromResult(HubStop(request)),
        "hub.listMachines" => Task.FromResult(HubListMachines(request)),
        "hub.getMachine" => Task.FromResult(HubGetMachine(request)),
        // 以下四项是 Windows 特有语义，macOS 侧尚未接入等价实现：
        // 返回结构化 not_supported，让 UI 明确降级而不是报"操作失败"。
        "net.proxyState" or "net.repairProxy" or "llm.state" or "llm.switch" or "llm.detect" or "llm.log" =>
            Task.FromResult(NotSupported(request)),
        _ => Task.FromResult(Response.Failure(request.Id, "unknown_method", "Unknown method")),
    };

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
            _hub = null;
            _audio = null;
            _store = null;
        }
    }
}
