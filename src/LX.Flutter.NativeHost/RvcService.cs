using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingXi.Flutter.NativeHost;

/// <summary>
/// One-shot key-authenticated SSH control. Configuration and keys stay outside
/// the repository in %LocalAppData%/LingXi/rvc-ssh.json and the user's .ssh folder.
/// No daemon, password prompt, shell launcher or remote resident controller.
/// </summary>
internal static class RvcService
{
    internal sealed record SshConfig(string Host, string User, string IdentityFile);
    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LingXi", "rvc-ssh.json");

    internal static object State()
    {
        try
        {
            return ParseReply(Send(LoadConfig(ConfigPath), "state"), "state");
        }
        catch (Exception ex)
        {
            return new
            {
                serverRunning = false,
                serviceRegistered = false,
                controllable = false,
                controlMode = "ssh",
                reason = PublicFailure(ex),
            };
        }
    }

    internal static object Switch(string action)
    {
        if (action is not ("start" or "stop"))
            throw new ArgumentException("action 只支持 start 或 stop");
        return ParseReply(Send(LoadConfig(ConfigPath), action), action);
    }

    internal static SshConfig LoadConfig(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("未配置 RVC SSH 控制");
        if (new FileInfo(path).Length > 4096) throw new ArgumentException("SSH 配置过大");
        var config = JsonSerializer.Deserialize<SshConfig>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("SSH 配置无效");
        if (string.IsNullOrEmpty(config.Host) || config.Host.Length > 253 ||
            !Regex.IsMatch(config.Host, @"\A[a-zA-Z0-9][a-zA-Z0-9.-]*\z") ||
            string.IsNullOrEmpty(config.User) || config.User.Length > 64 ||
            !Regex.IsMatch(config.User, @"\A[a-zA-Z_][a-zA-Z0-9_.-]*\z") ||
            string.IsNullOrEmpty(config.IdentityFile) || !Path.IsPathFullyQualified(config.IdentityFile))
            throw new ArgumentException("SSH 主机、用户或密钥路径无效");
        if (!File.Exists(config.IdentityFile)) throw new FileNotFoundException("找不到 SSH 密钥文件");
        return config;
    }

    private static string Send(SshConfig config, string action)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "OpenSSH", "ssh.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("未安装 Windows OpenSSH 客户端");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[]
        {
            "-n", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=5",
            "-o", "StrictHostKeyChecking=yes", "-o", "IdentitiesOnly=yes",
            "-o", "ServerAliveInterval=2", "-o", "ServerAliveCountMax=2",
            "-i", config.IdentityFile, $"{config.User}@{config.Host}",
            // Only internal whitelisted verbs enter this fixed remote command.
            $"~/.local/bin/rvc-server-control {action}",
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("SSH 无法启动");
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            // Windows OpenSSH with redirected pipes can reply immediately but
            // wait indefinitely for exit/EOF (verified on the deployed WinExe).
            // The helper emits exactly one final JSON line after its operation;
            // consume that acknowledgement rather than waiting for pipe EOF.
            var reply = process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
            if (reply is null) throw new IOException("SSH 未返回控制结果");
            if (reply.Length > 8192) throw new IOException("SSH 控制结果过大");
            return reply;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            process.WaitForExit(1000);
            // Observe pipe errors; do not expose stderr (may include local paths).
            try { stderr.Wait(1000); _ = stderr.Exception; } catch { }
        }
    }

    internal static JsonElement ParseReply(string json, string action)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
            throw new IOException("远端 RVC 控制失败");
        if (action == "state")
        {
            foreach (var key in new[] { "serverRunning", "serviceRegistered", "controllable" })
                if (!root.TryGetProperty(key, out var value) ||
                    value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new IOException("远端状态字段无效");
        }
        else if (!root.TryGetProperty("action", out var verb) || verb.GetString() != action ||
                 !root.TryGetProperty("exitCode", out var exitCode) || !exitCode.TryGetInt32(out _))
            throw new IOException("远端操作回执无效");
        return root.Clone();
    }

    private static string PublicFailure(Exception error) => error switch
    {
        TimeoutException => "SSH 控制连接超时，请确认 Mac 已开机、已登录且 SSH 可达",
        FileNotFoundException => "RVC SSH 控制未配置完整；请检查本机配置、密钥和 OpenSSH 客户端",
        ArgumentException or JsonException => "RVC SSH 控制配置或回执无效，请检查本机配置",
        _ => "无法通过 SSH 连接 RVC 服务器，请检查网络、密钥授权和已登记的主机指纹",
    };
}
