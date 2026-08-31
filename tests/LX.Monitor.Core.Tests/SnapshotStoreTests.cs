using LingXi.Monitor.Core;
using Xunit;

namespace LX.Monitor.Core.Tests;

public class SnapshotStoreTests
{
    private static SnapshotEnvelope Envelope(string name, DateTimeOffset at) =>
        new(new Snapshot { Name = name }, "tok", at);

    [Fact]
    public void Online_Within_Timeout_Offline_After()
    {
        var store = new SnapshotStore();
        store.SetOfflineTimeout(TimeSpan.FromSeconds(30));
        var t0 = DateTimeOffset.Now;

        store.Upsert(Envelope("a", t0));

        Assert.True(store.IsOnline("a", t0.AddSeconds(29)));
        Assert.False(store.IsOnline("a", t0.AddSeconds(31)));
    }

    [Fact]
    public void OnlineChanged_Fires_On_Transitions()
    {
        var store = new SnapshotStore();
        store.SetOfflineTimeout(TimeSpan.FromSeconds(10));
        var events = new List<(string Name, bool Online)>();
        store.OnlineChanged += (name, online) => events.Add((name, online));

        var t0 = DateTimeOffset.Now;
        store.Upsert(Envelope("m", t0));                  // → online
        store.Sweep(t0.AddSeconds(20));                   // → offline
        store.Upsert(Envelope("m", t0.AddSeconds(30)));   // → online again

        Assert.Equal([("m", true), ("m", false), ("m", true)], events);
    }

    [Fact]
    public void Get_Returns_Latest_Envelope()
    {
        var store = new SnapshotStore();
        var t0 = DateTimeOffset.Now;
        store.Upsert(Envelope("m", t0));
        store.Upsert(new SnapshotEnvelope(new Snapshot { Name = "m", AgentTs = 2 }, "tok", t0.AddSeconds(5)));

        var latest = store.Get("m");
        Assert.Equal(2, latest?.Snapshot.AgentTs);
    }
}
