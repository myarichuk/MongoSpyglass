using System.Diagnostics;
using System.Text.Json;
using Spyglass.Proxy.Config;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

/// <summary>
/// 06 §2 with the Node.js driver: the floor (6.4.0, first release with serverConnectionId), latest 6.x and 7.x.
/// tests/node-conformance/drive.mjs runs the workload and prints what the driver observed; this test checks
/// it against the proxy's records. Needs node and `npm install` in tests/node-conformance.
/// </summary>
public sealed class NodeDriverTests(ObservationFixture f) : IClassFixture<ObservationFixture>
{
    private static readonly string? Dir = FindDir();

    private static string? FindDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "node-conformance");
            if (File.Exists(Path.Combine(p, "drive.mjs")))
                return Directory.Exists(Path.Combine(p, "node_modules")) ? p : null;
        }
        return null;
    }

    [Theory]
    [InlineData("mongodb-floor")]
    [InlineData("mongodb-6")]
    [InlineData("mongodb-7")]
    public async Task Node_driver_through_proxy(string driver)
    {
        if (Dir == null)
            Assert.Skip("run `npm install` in tests/node-conformance");

        var app = "node-" + driver + "-" + Guid.NewGuid().ToString("N")[..4];
        var psi = new ProcessStartInfo("node") { WorkingDirectory = Dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "drive.mjs", driver, f.Proxy.Uri("rs0"), app }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(p.ExitCode == 0, await stderr);

        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        string K(JsonElement e) => e.GetProperty("kind").GetString()!;

        var done = lines.Single(l => K(l) == "done");
        Assert.Equal(250, done.GetProperty("inserted").GetInt32());
        Assert.Equal(250, done.GetProperty("found").GetInt32());

        // topology discovered only through proxy listeners (R1)
        var proxyAddresses = f.Proxy.Server.Topology.Members.Select(m => m.Advertised).ToHashSet();
        var servers = lines.Single(l => K(l) == "servers").GetProperty("addresses").EnumerateArray().Select(a => HostPort.Parse(a.GetString()!).ToString()).ToList();
        Assert.Equal(3, servers.Count);
        Assert.Subset(proxyAddresses, servers.ToHashSet());

        // discovery document visible on the heartbeat event (02 §2.3 activation path)
        var heartbeats = lines.Where(l => K(l) == "heartbeat").ToList();
        Assert.NotEmpty(heartbeats);
        foreach (var hb in heartbeats)
        {
            var sg = hb.GetProperty("spyglass");
            Assert.Equal(JsonValueKind.Object, sg.ValueKind);
            Assert.Equal(f.Proxy.Server.ProxyId.ToString(), sg.GetProperty("proxyId").GetString());
            Assert.Equal(HostPort.Parse(hb.GetProperty("address").GetString()!).ToString(), sg.GetProperty("member").GetString());
        }

        // join key (02 §3): every command event matches exactly one wire op
        var cmds = lines.Where(l => K(l) == "cmd").ToList();
        Assert.True(cmds.Count > 15, $"only {cmds.Count} commands");
        Assert.All(cmds, c => Assert.Equal(JsonValueKind.Number, c.GetProperty("serverConnectionId").ValueKind)); // ≥ 6.4.0
        // Node's command-event `address` is the socket's resolved remote IP:port, not the dialled host (verified
        // 6.4.0–7.7.0). The exporter maps it to the proxy's own member string from $spyglass (05 §4): by port,
        // which is unique among one proxy's listeners.
        var memberByPort = heartbeats
            .GroupBy(hb => HostPort.Parse(hb.GetProperty("address").GetString()!).Port)
            .ToDictionary(g => g.Key, g => g.First().GetProperty("spyglass").GetProperty("member").GetString()!);
        Assert.Contains(cmds, c => c.GetProperty("address").GetString()!.StartsWith("127.0.0.1:"));
        (string?, long, int) KeyOf(JsonElement c) =>
            (memberByPort[HostPort.Parse(c.GetProperty("address").GetString()!).Port], c.GetProperty("serverConnectionId").GetInt64(), c.GetProperty("requestId").GetInt32());
        var index = await f.Proxy.WaitForKeysAsync("op", o =>
            (o.GetProperty("member").GetString(), o.GetProperty("serverConnectionId").ValueKind == JsonValueKind.Number ? o.GetProperty("serverConnectionId").GetInt64() : -1, o.GetProperty("requestId").GetInt32()),
            cmds.Select(KeyOf).ToList());
        var misses = new List<string>();
        foreach (var c in cmds)
        {
            var key = KeyOf(c);
            var m = index[key].ToList();
            if (m.Count != 1) { misses.Add($"{c.GetProperty("commandName")} {key} x{m.Count}"); continue; }
            Assert.Equal(c.GetProperty("commandName").GetString(), m[0].GetProperty("command").GetString());
            Assert.Equal(c.GetProperty("databaseName").GetString(), m[0].GetProperty("db").GetString());
        }
        Assert.Empty(misses);

        // streaming hello (moreToCome) on the driver's monitoring connections: one op with stream=true, then
        // `stream` records for each further reply (01 §7, 06 §2.5)
        await ReplicaSetTests.WaitUntil(() => f.Proxy.RecordsOf("stream").Any(), TimeSpan.FromSeconds(10));
        Assert.Contains(f.Proxy.RecordsOf("op"), o => o.GetProperty("stream").GetBoolean() && o.GetProperty("command").GetString() == "hello");

        // handshake marker (02 §2.4) where the driver has appendMetadata
        var conns = f.Proxy.RecordsOf("conn").Where(r => r.GetProperty("app").ValueKind == JsonValueKind.String && r.GetProperty("app").GetString() == app).ToList();
        Assert.NotEmpty(conns);
        Assert.All(conns, c => Assert.StartsWith("nodejs", c.GetProperty("driver").GetString()));
        var markers = conns.Select(c => c.GetProperty("exporter")).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).Distinct().ToList();
        Assert.True(markers.Count == 0 || markers.SequenceEqual(["spyglass-node"]), string.Join(',', markers));
        if (driver != "mongodb-floor")
            Assert.Equal(["spyglass-node"], markers);
    }
}
