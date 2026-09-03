using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LingXi.Flutter.NativeHost;

/// <summary>Windows 用户代理诊断与安全修复。只操作 HKCU，不需要管理员权限。</summary>
internal sealed class SystemProxyService
{
    private const string InternetSettingsKey =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";
    private const int InternetOptionRefresh = 37;
    private const int InternetOptionSettingsChanged = 39;
    private const int ProbeTimeoutMs = 500;
    private const int MaxEndpoints = 16;
    private const int MaxTextLength = 2048;

    public async Task<ProxyState> ReadAsync()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: false);
        var enabled = ReadDword(key, "ProxyEnable") == 1;
        var server = ReadText(key, "ProxyServer");
        var autoConfigUrl = ReadText(key, "AutoConfigURL");
        var endpoints = ParseEndpoints(server);
        foreach (var endpoint in endpoints)
        {
            endpoint.Reachable = await IsReachableAsync(endpoint.Host, endpoint.Port);
        }

        var winHttp = await ReadWinHttpAsync();
        var diagnosis = !string.IsNullOrWhiteSpace(autoConfigUrl)
            ? "pac_configured"
            : !enabled
                ? "none"
                : endpoints.Any(e => e.Reachable)
                    ? "healthy"
                    : endpoints.Count > 0
                        ? "stale_proxy"
                        : "unknown";

        return new ProxyState(enabled, server, autoConfigUrl, endpoints, winHttp, diagnosis);
    }

    public async Task<RepairResult> RepairAsync(bool resetWinHttp)
    {
        var before = await ReadAsync();
        if (before.Diagnosis == "healthy")
        {
            return new RepairResult(false, false, false, false, "proxy_alive", before);
        }
        if (before.Diagnosis != "stale_proxy")
        {
            return new RepairResult(false, false, false, false, "proxy_not_stale", before);
        }

        using (var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true)
               ?? throw new InvalidOperationException("Internet Settings registry key unavailable"))
        {
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        }

        var refreshed = RefreshInternetSettings();
        var winHttpReset = resetWinHttp && await ResetWinHttpAsync();
        var after = await ReadAsync();
        return new RepairResult(true, refreshed, winHttpReset, true, null, after);
    }

    private static int ReadDword(RegistryKey? key, string name) =>
        key?.GetValue(name) is int value ? value : 0;

    private static string ReadText(RegistryKey? key, string name)
    {
        var value = key?.GetValue(name)?.ToString()?.Trim() ?? string.Empty;
        return value.Length <= MaxTextLength ? value : value[..MaxTextLength];
    }

    private static List<ProxyEndpoint> ParseEndpoints(string value)
    {
        var result = new List<ProxyEndpoint>();
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            var scheme = separator > 0 ? part[..separator].Trim().ToLowerInvariant() : "proxy";
            var address = separator > 0 ? part[(separator + 1)..].Trim() : part;
            if (TryParseEndpoint(address, scheme, out var endpoint))
            {
                if (!result.Any(e => e.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) && e.Port == endpoint.Port))
                    result.Add(endpoint);
            }
            if (result.Count >= MaxEndpoints) break;
        }
        return result;
    }

    private static bool TryParseEndpoint(string value, string scheme, out ProxyEndpoint endpoint)
    {
        endpoint = null!;
        if (value.Length == 0 || value.Length > 512) return false;
        var candidate = value.Contains("://", StringComparison.Ordinal)
            ? value
            : $"http://{value}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.Port is < 1 or > 65535)
            return false;
        if (uri.UserInfo.Length > 0) return false;
        endpoint = new ProxyEndpoint(scheme, uri.Host, uri.Port);
        return true;
    }

    private static async Task<bool> IsReachableAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(ProbeTimeoutMs);
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool RefreshInternetSettings() =>
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0) &&
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);

    private static async Task<string> ReadWinHttpAsync()
    {
        var output = await RunNetshAsync("show", "proxy");
        if (output.Contains("Direct access", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("直接访问", StringComparison.OrdinalIgnoreCase) ||
            output.Contains(": -", StringComparison.OrdinalIgnoreCase))
            return "direct";
        return output == "不可读取" ? output : "configured";
    }

    private static async Task<bool> ResetWinHttpAsync()
    {
        var output = await RunNetshAsync("reset", "proxy");
        return output.Contains("Direct access", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("直接访问", StringComparison.OrdinalIgnoreCase) ||
               !output.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> RunNetshAsync(params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("winhttp");
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return "不可读取";
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var output = await outputTask;
            if (string.IsNullOrWhiteSpace(output)) output = await errorTask;
            output = output.Trim();
            return output.Length <= 512 ? output : output[..512];
        }
        catch
        {
            return "不可读取";
        }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}

internal sealed class ProxyState
{
    public ProxyState(bool enabled, string server, string autoConfigUrl,
        IReadOnlyList<ProxyEndpoint> endpoints, string winHttpProxy, string diagnosis)
    {
        ProxyEnabled = enabled;
        ProxyServer = server;
        AutoConfigUrl = autoConfigUrl;
        Endpoints = endpoints;
        WinHttpProxy = winHttpProxy;
        Diagnosis = diagnosis;
    }

    public bool ProxyEnabled { get; }
    public string ProxyServer { get; }
    public string AutoConfigUrl { get; }
    public IReadOnlyList<ProxyEndpoint> Endpoints { get; }
    public string WinHttpProxy { get; }
    public string Diagnosis { get; }
}

internal sealed class ProxyEndpoint
{
    public ProxyEndpoint(string scheme, string host, int port)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
    }

    public string Scheme { get; }
    public string Host { get; }
    public int Port { get; }
    public bool Reachable { get; set; }
}

internal sealed class RepairResult
{
    public RepairResult(bool disabled, bool refreshed, bool winHttpReset,
        bool repaired, string? refusal, ProxyState state)
    {
        Disabled = disabled;
        WinInetRefreshed = refreshed;
        WinHttpReset = winHttpReset;
        Repaired = repaired;
        Refusal = refusal;
        State = state;
    }

    public bool Disabled { get; }
    public bool WinInetRefreshed { get; }
    public bool WinHttpReset { get; }
    public bool Repaired { get; }
    public string? Refusal { get; }
    public ProxyState State { get; }
}
