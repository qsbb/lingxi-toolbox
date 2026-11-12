using System.Text.Json;
using LingXi.Flutter.NativeHost;
using Xunit;

public class RvcServiceTests
{
    [Fact]
    public void StateReplyRequiresTypedBooleans()
    {
        var state = RvcService.ParseReply("""{"serverRunning":false,"serviceRegistered":true,"controllable":true,"controlMode":"ssh"}""", "state");
        Assert.False(state.GetProperty("serverRunning").GetBoolean());
        Assert.Equal("ssh", state.GetProperty("controlMode").GetString());
        Assert.Throws<IOException>(() => RvcService.ParseReply("""{"serverRunning":"false"}""", "state"));
        Assert.Throws<IOException>(() => RvcService.ParseReply("""{"error":"failed"}""", "state"));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public void SwitchReplyMustAcknowledgeRequestedAction(string action)
    {
        var json = JsonSerializer.Serialize(new { action, exitCode = 0, stdout = "", stderr = "" });
        Assert.Equal(0, RvcService.ParseReply(json, action).GetProperty("exitCode").GetInt32());
        Assert.Throws<IOException>(() => RvcService.ParseReply("{}", action));
        Assert.Throws<IOException>(() => RvcService.ParseReply("""{"action":"other","exitCode":0}""", action));
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("start;whoami")]
    public void ArbitraryActionsAreRejectedBeforeAnySshOrConfigAccess(string action)
        => Assert.Throws<ArgumentException>(() => RvcService.Switch(action));

    [Theory]
    [InlineData("-oProxyCommand=anything", "lingxi")]
    [InlineData("host;whoami", "lingxi")]
    [InlineData("192.168.5.66", "-root")]
    [InlineData("192.168.5.66", "user@otherhost")]
    public void SshConfigurationRejectsArgumentAndCommandInjection(string host, string user)
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(temp);
        try
        {
            var key = Path.Combine(temp, "key"); File.WriteAllText(key, "fixture-not-a-key");
            var config = Path.Combine(temp, "rvc-ssh.json");
            File.WriteAllText(config, JsonSerializer.Serialize(new { Host = host, User = user, IdentityFile = key }));
            Assert.Throws<ArgumentException>(() => RvcService.LoadConfig(config));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public void ValidConfigurationIsLocalOnly()
    {
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(temp);
        try
        {
            var key = Path.Combine(temp, "key"); File.WriteAllText(key, "fixture-not-a-key");
            var path = Path.Combine(temp, "rvc-ssh.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Host = "192.168.5.66", User = "lingxi", IdentityFile = key }));
            var config = RvcService.LoadConfig(path);
            Assert.Equal(key, config.IdentityFile);
            Assert.Equal("192.168.5.66", config.Host);
        }
        finally { Directory.Delete(temp, true); }
    }
}
