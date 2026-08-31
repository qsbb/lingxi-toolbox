using System.Collections.Concurrent;

namespace LingXi.Monitor.Core;

/// <summary>
/// 最新快照表 + 在线判定（对齐 model.js 的 records/lastSeen 语义）。
/// - 每台机器只存最新快照；
/// - OfflineTimeout 内有上报 = 在线；
/// - 状态翻转（上线/掉线）触发 OnlineChanged。
/// </summary>
public sealed class SnapshotStore
{
    private readonly ConcurrentDictionary<string, SnapshotEnvelope> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _knownOnline = new(StringComparer.Ordinal);
    private TimeSpan _offlineTimeout = TimeSpan.FromSeconds(30);

    /// <summary>(name, isOnline) 状态翻转事件。</summary>
    public event Action<string, bool>? OnlineChanged;

    public TimeSpan OfflineTimeout => _offlineTimeout;

    public void SetOfflineTimeout(TimeSpan timeout) => _offlineTimeout = timeout;

    public void Upsert(SnapshotEnvelope envelope)
    {
        var name = ResolveName(envelope.Snapshot);
        envelope.Snapshot.Name = name;
        _entries[name] = envelope;
        SetOnline(name, true);
    }

    public bool IsOnline(string name, DateTimeOffset now) =>
        _entries.TryGetValue(name, out var e) && now - e.ReceivedAt <= _offlineTimeout;

    public SnapshotEnvelope? Get(string name) =>
        _entries.TryGetValue(name, out var e) ? e : null;

    public IReadOnlyList<SnapshotEnvelope> GetAll() =>
        [.. _entries.Values.OrderByDescending(e => e.ReceivedAt)];

    public IReadOnlyList<string> GetNames() => [.. _entries.Keys];

    /// <summary>周期巡检（UI 定时器调用）：发现掉线/恢复并触发事件。</summary>
    public void Sweep(DateTimeOffset now)
    {
        foreach (var (name, entry) in _entries)
        {
            SetOnline(name, now - entry.ReceivedAt <= _offlineTimeout);
        }
    }

    private void SetOnline(string name, bool online)
    {
        if (_knownOnline.TryGetValue(name, out var previous) && previous == online)
        {
            return;
        }
        _knownOnline[name] = online;
        OnlineChanged?.Invoke(name, online);
    }

    private static string ResolveName(Snapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Name))
        {
            return snapshot.Name.Trim();
        }
        return string.IsNullOrWhiteSpace(snapshot.Os?.Hostname)
            ? "unknown"
            : snapshot.Os!.Hostname!.Trim();
    }
}
