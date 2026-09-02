using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LingXi.Audio;
using LingXi.Monitor.Core;

namespace LingXi.Flutter.NativeHost;

/// <summary>
/// Flutter 与 Windows 原生能力之间的 JSON Lines 协议宿主。
/// stdout 只承载协议，诊断信息走 stderr；每行请求最多 256 KiB，响应最多由调用方限制。
/// </summary>
internal static class Program
{
    private const int MaxRequestLineLength = 256 * 1024;
    private const int MaxMethodLength = 80;
    private const int MaxRequestIdLength = 128;
    private const int MaxParameterCount = 32;
    private const int MaxResponseLineLength = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static IAudioEndpointService? _audio;
    private static SystemMetricsCollector? _metrics;
    private static LxHub? _hub;
    private static SnapshotStore? _store;
    private static readonly object Gate = new();

    private static async Task Main()
    {
        Console.Error.WriteLine("LingXi Flutter NativeHost started");
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
                line = await ReadLineLimitedAsync(reader, MaxRequestLineLength);
            }
            catch (ProtocolException ex)
            {
                Console.Error.WriteLine($"protocol error: {ex.Code}");
                await WriteResponseAsync(writer, Response.Failure(null, ex.Code, "Invalid request"));
                continue;
            }

            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            Request? request = null;
            Response response;
            try
            {
                request = JsonSerializer.Deserialize<Request>(line, Json)
                    ?? throw new ProtocolException("invalid_json", "request is null");
                Validate(request);
                response = await HandleAsync(request);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"request failed: {ex.GetType().Name}");
                response = Response.Failure(request?.Id, ErrorCode(ex), PublicError(ex));
            }

            await WriteResponseAsync(writer, response);
        }

        Shutdown();
    }

    private static async Task WriteResponseAsync(StreamWriter writer, Response response)
    {
        var json = JsonSerializer.Serialize(response, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxResponseLineLength)
        {
            json = JsonSerializer.Serialize(
                Response.Failure(response.Id, "response_too_large", "Response exceeds size limit"), Json);
        }
        await writer.WriteLineAsync(json);
    }

    private static async Task<string?> ReadLineLimitedAsync(TextReader reader, int maxLength)
    {
        var buffer = new char[1024];
        var line = new StringBuilder(Math.Min(maxLength, 4096));
        var readAny = false;
        var tooLong = false;

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0)
            {
                if (!readAny) return null;
                if (tooLong) throw new ProtocolException("request_too_large", "request exceeds size limit");
                return line.ToString().TrimEnd('\r');
            }

            readAny = true;
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    if (tooLong) throw new ProtocolException("request_too_large", "request exceeds size limit");
                    return line.ToString().TrimEnd('\r');
                }

                if (line.Length >= maxLength)
                {
                    tooLong = true;
                }
                else if (!tooLong)
                {
                    line.Append(c);
                }
            }
        }
    }

    private static void Validate(Request request)
    {
        var method = request.Method?.Trim() ?? string.Empty;
        if (method.Length == 0 || method.Length > MaxMethodLength)
            throw new ProtocolException("invalid_method", "method is invalid");
        request.Method = method;

        if (request.Id is { Length: > MaxRequestIdLength })
            throw new ProtocolException("invalid_request_id", "id is too long");
        if (request.Parameters.Count > MaxParameterCount)
            throw new ProtocolException("too_many_parameters", "too many parameters");
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
                    "audio.list", "audio.default", "audio.setDefault", "metrics.snapshot",
                    "hub.start", "hub.stop", "hub.listMachines", "hub.getMachine",
                },
            })),
            "audio.list" => Task.FromResult(AudioList(request)),
            "audio.default" => Task.FromResult(AudioDefault(request)),
            "audio.setDefault" => Task.FromResult(AudioSetDefault(request)),
            "metrics.snapshot" => Task.FromResult(MetricsSnapshot(request)),
            "hub.start" => Task.FromResult(HubStart(request)),
            "hub.stop" => Task.FromResult(HubStop(request)),
            "hub.listMachines" => Task.FromResult(HubListMachines(request)),
            "hub.getMachine" => Task.FromResult(HubGetMachine(request)),
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
            return Response.Failure(request.Id, "audio_unavailable", PublicError(ex));
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
            return Response.Failure(request.Id, "audio_unavailable", PublicError(ex));
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
            return Response.Failure(request.Id, "audio_set_failed", PublicError(ex));
        }
    }

    private static Response MetricsSnapshot(Request request)
    {
        try
        {
            _metrics ??= new SystemMetricsCollector();
            _metrics.MachineName = Optional(request.Parameters, "name")?.Trim() is { Length: > 0 } name
                ? name[..Math.Min(name.Length, 128)]
                : Environment.MachineName;
            var snapshot = _metrics.Collect();
            return snapshot is null
                ? Response.Failure(request.Id, "metrics_empty", "No usable metric was collected")
                : Response.Success(request.Id, snapshot);
        }
        catch (Exception ex)
        {
            return Response.Failure(request.Id, "metrics_failed", PublicError(ex));
        }
    }

    private static Response HubStart(Request request)
    {
        lock (Gate)
        {
            try
            {
                _hub?.Dispose();
                var port = Math.Clamp(OptionalInt(request.Parameters, "port") ?? 2536, 1024, 65535);
                var bindLan = OptionalBool(request.Parameters, "bindLan") ?? true;
                var token = Optional(request.Parameters, "token")?.Trim();
                if (string.IsNullOrWhiteSpace(token)) token = TokenGen.NewToken();
                if (token.Length > 512) throw new ProtocolException("invalid_token", "token is too long");

                var offlineTimeout = Math.Clamp(OptionalInt(request.Parameters, "offlineTimeoutSec") ?? 30, 5, 86400);
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
                _hub = new LxHub(options, _store);
                _hub.Start();
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
                return Response.Failure(request.Id, "hub_start_failed", PublicError(ex));
            }
        }
    }

    private static Response HubStop(Request request)
    {
        lock (Gate)
        {
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
            return Response.Failure(request.Id, "invalid_parameters", PublicError(ex));
        }
    }

    private static AudioDto ToAudio(AudioEndpoint endpoint) => new(
        endpoint.Id,
        endpoint.Name,
        endpoint.State.ToString().ToLowerInvariant());

    private static DataFlow GetFlow(IReadOnlyDictionary<string, object?> parameters) =>
        string.Equals(Optional(parameters, "flow"), "capture", StringComparison.OrdinalIgnoreCase)
            ? DataFlow.Capture
            : DataFlow.Render;

    private static string Required(IReadOnlyDictionary<string, object?> parameters, string key, int maxLength) =>
        Optional(parameters, key) is { Length: > 0 } value && value.Length <= maxLength
            ? value
            : throw new ArgumentException($"Invalid parameter: {key}");

    private static string? Optional(IReadOnlyDictionary<string, object?> parameters, string key) =>
        parameters.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int? OptionalInt(IReadOnlyDictionary<string, object?> parameters, string key) =>
        int.TryParse(Optional(parameters, key), out var value) ? value : null;

    private static bool? OptionalBool(IReadOnlyDictionary<string, object?> parameters, string key) =>
        bool.TryParse(Optional(parameters, key), out var value) ? value : null;

    private static string ErrorCode(Exception ex) => ex switch
    {
        ProtocolException protocol => protocol.Code,
        ArgumentException => "invalid_parameters",
        _ => "native_error",
    };

    private static string PublicError(Exception ex) => ex switch
    {
        ProtocolException => "Invalid request",
        ArgumentException => "Invalid parameters",
        _ => "Native operation failed",
    };

    private static void Shutdown()
    {
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

internal sealed class Request
{
    public string? Id { get; set; }
    public string? Method { get; set; }
    public Dictionary<string, object?>? Params { get; set; }
    [JsonIgnore]
    public Dictionary<string, object?> Parameters => Params ??= new(StringComparer.Ordinal);
}

internal sealed record Response(string? Id, bool Ok, object? Result, ErrorBody? Error)
{
    public static Response Success(string? id, object result) => new(id, true, result, null);
    public static Response Failure(string? id, string code, string message) =>
        new(id, false, null, new ErrorBody(code, message));
}

internal sealed record ErrorBody(string Code, string Message);
internal sealed record AudioDto(string Id, string Name, string State);

internal sealed class ProtocolException : Exception
{
    public ProtocolException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}
