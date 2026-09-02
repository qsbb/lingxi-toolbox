namespace LingXi.Monitor.Core;

/// <summary>
/// 最新快照表 + 在线判定（对齐 model.js 的 records/lastSeen 语义）。
/// 写入、时间戳顺序和在线状态转换在同一把锁内完成，事件在锁外触发。
/// </summary>
public sealed class SnapshotStore
{
    private const int MaxEntries = 512;
    private readonly object _gate = new();
    private readonly Dictionary<string, SnapshotEnvelope> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _knownOnline = new(StringComparer.Ordinal);
    private TimeSpan _offlineTimeout = TimeSpan.FromSeconds(30);

    /// <summary>(name, isOnline) 状态翻转事件。</summary>
    public event Action<string, bool>? OnlineChanged;

    public TimeSpan OfflineTimeout
    {
        get { lock (_gate) return _offlineTimeout; }
    }

    public void SetOfflineTimeout(TimeSpan timeout)
    {
        var bounded = timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(30)
            : timeout < TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : timeout > TimeSpan.FromDays(1)
                    ? TimeSpan.FromDays(1)
                    : timeout;
        lock (_gate) _offlineTimeout = bounded;
    }

    /// <summary>
    /// 写入一台机器的最新快照。带有更早 agent_ts 的重放包被拒绝；没有时间戳的旧 agent
    /// 仍可兼容接入，但不会覆盖一个带时间戳的新快照。
    /// </summary>
    public bool TryUpsert(SnapshotEnvelope envelope, out bool stale)
    {
        Action<string, bool>? transition = null;
        string name;
        lock (_gate)
        {
            name = ResolveName(envelope.Snapshot);
            envelope.Snapshot.Name = name;
            stale = false;

            if (_entries.TryGetValue(name, out var previous))
            {
                var oldTs = previous.Snapshot.AgentTs;
                var newTs = envelope.Snapshot.AgentTs;
                if (oldTs.HasValue && newTs.HasValue && newTs.Value < oldTs.Value)
                {
                    stale = true;
                    return false;
                }
                if (oldTs.HasValue && !newTs.HasValue)
                {
                    stale = true;
                    return false;
                }
            }
            else if (_entries.Count >= MaxEntries)
            {
                var evicted = _entries
                    .OrderBy(pair => pair.Value.ReceivedAt)
                    .Select(pair => pair.Key)
                    .FirstOrDefault();
                if (evicted is not null)
                {
                    _entries.Remove(evicted);
                    _knownOnline.Remove(evicted);
                }
            }

            _entries[name] = envelope;
            if (SetOnlineLocked(name, true)) transition = OnlineChanged;
        }

        NotifyOnlineChanged(transition, name, true);
        stale = false;
        return true;
    }

    /// <summary>兼容旧调用方；被判定为过期的快照会被静默丢弃。</summary>
    public void Upsert(SnapshotEnvelope envelope) => TryUpsert(envelope, out _);

    public bool IsOnline(string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(name, out var entry) && now - entry.ReceivedAt <= _offlineTimeout;
        }
    }

    public SnapshotEnvelope? Get(string name)
    {
        lock (_gate) return _entries.TryGetValue(name, out var entry) ? entry : null;
    }

    public IReadOnlyList<SnapshotEnvelope> GetAll(int maxCount = MaxEntries)
    {
        lock (_gate)
        {
            return _entries.Values
                .OrderByDescending(entry => entry.ReceivedAt)
                .Take(Math.Clamp(maxCount, 1, MaxEntries))
                .ToArray();
        }
    }

    public IReadOnlyList<string> GetNames()
    {
        lock (_gate) return _entries.Keys.ToArray();
    }

    /// <summary>周期巡检：发现掉线/恢复并触发事件。</summary>
    public void Sweep(DateTimeOffset now)
    {
        List<string>? changed = null;
        lock (_gate)
        {
            foreach (var (name, entry) in _entries)
            {
                var online = now - entry.ReceivedAt <= _offlineTimeout;
                if (SetOnlineLocked(name, online)) (changed ??= []).Add(name);
            }
        }

        if (changed is null) return;
        foreach (var name in changed)
        {
            NotifyOnlineChanged(OnlineChanged, name, IsOnline(name, now));
        }
    }

    private void NotifyOnlineChanged(Action<string, bool>? handler, string name, bool online)
    {
        foreach (var callback in handler?.GetInvocationList().OfType<Action<string, bool>>() ?? [])
        {
            try
            {
                callback(name, online);
            }
            catch
            {
                // A status observer cannot invalidate the committed store update.
            }
        }
    }

    private bool SetOnlineLocked(string name, bool online)
    {
        if (_knownOnline.TryGetValue(name, out var previous) && previous == online) return false;
        _knownOnline[name] = online;
        return true;
    }

    private static string ResolveName(Snapshot snapshot)
    {
        var name = !string.IsNullOrWhiteSpace(snapshot.Name) ? snapshot.Name.Trim() : snapshot.Os?.Hostname?.Trim();
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name[..Math.Min(name.Length, 128)];
    }
}
