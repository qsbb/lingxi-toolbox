namespace LingXi.Core.Update;

/// <summary>Velopack 更新：检查 → 下载增量 → 重启应用；未打包运行环境安全降级（开发文档 7.6 / 13.3）。</summary>
public sealed class UpdateService
{
    /// <summary>检查并应用更新。返回 true 表示已请求重启安装。updateUrl 为 Velopack 发布源。</summary>
    public async Task<bool> CheckAndApplyAsync(string? updateUrl = null, Action<string>? log = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(updateUrl))
            {
                log?.Invoke("未配置更新源，跳过更新检查。");
                return false;
            }

            var manager = new global::Velopack.UpdateManager(updateUrl);
            if (!manager.IsInstalled)
            {
                log?.Invoke("当前为未打包运行，跳过更新检查。");
                return false;
            }

            var info = await manager.CheckForUpdatesAsync();
            if (info is null || info.TargetFullRelease is null)
            {
                log?.Invoke("已是最新版本。");
                return false;
            }

            await manager.DownloadUpdatesAsync(info);
            manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("更新失败：" + ex.Message);
            return false;
        }
    }
}
