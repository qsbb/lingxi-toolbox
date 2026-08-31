using System.IO;
using System.IO.Pipes;
using System.Text;

namespace LingXi.Core;

/// <summary>单实例：Mutex 判定 + 命名管道转发激活请求（开发文档 7.7）。</summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\LingXi.Toolbox.SingleInstance";
    public const string PipeName = "LingXi.Toolbox.Activate";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    /// <summary>二次实例发出的激活请求。</summary>
    public event Action? ActivateRequested;

    /// <summary>尝试成为首实例；失败时向首实例发送激活请求并返回 false。</summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (createdNew)
        {
            StartPipeServer();
            return true;
        }
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine("activate");
        }
        catch
        {
            // 首实例可能正在退出：本次启动直接结束
        }
        return false;
    }

    private void StartPipeServer()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    if (await reader.ReadLineAsync(token) == "activate")
                    {
                        ActivateRequested?.Invoke();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 单次连接失败不退出服务循环
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // 非 owning thread 释放会抛：忽略
        }
        _mutex?.Dispose();
    }
}
