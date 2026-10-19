using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Spyglass.Proxy.Tests.Infrastructure;

/// <summary>Locates mongod: <c>SPYGLASS_MONGOD_BIN</c> or <c>tools/mongod/.cache/*/bin</c> (tools/mongod/fetch.sh).</summary>
public static class MongodBinaries
{
    public static string? BinDirectory { get; } = Find();

    private static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("SPYGLASS_MONGOD_BIN");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "mongod")))
            return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var cache = Path.Combine(dir.FullName, "tools", "mongod", ".cache");
            if (!Directory.Exists(cache)) continue;
            var bin = Directory.GetDirectories(cache).OrderDescending()
                .Select(v => Path.Combine(v, "bin"))
                .FirstOrDefault(b => File.Exists(Path.Combine(b, "mongod")));
            if (bin != null) return bin;
        }
        return null;
    }

    public static string Mongod => Path.Combine(BinDirectory ?? throw new InvalidOperationException(Missing), "mongod");

    public const string Missing = "mongod not found: run tools/mongod/fetch.sh or set SPYGLASS_MONGOD_BIN";

    public static void SkipIfMissing()
    {
        if (BinDirectory == null)
            Assert.Skip(Missing);
    }
}

public static class Ports
{
    private static int _next = 40000 + Random.Shared.Next(0, 10000);

    /// <summary>Reserves <paramref name="count"/> consecutive free TCP ports on loopback.</summary>
    public static int FreeRange(int count)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var start = Interlocked.Add(ref _next, count + 1) - count;
            if (start + count > 65000)
            {
                Interlocked.Exchange(ref _next, 40000);
                continue;
            }
            if (Enumerable.Range(start, count).All(IsFree))
                return start;
        }
        throw new InvalidOperationException("no free port range");
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

public sealed class MongodProcess : IAsyncDisposable
{
    private readonly Process _process;
    public int Port { get; }
    public string DbPath { get; }

    private MongodProcess(Process process, int port, string dbPath)
    {
        _process = process;
        Port = port;
        DbPath = dbPath;
    }

    public static MongodProcess Start(int port, string root, IEnumerable<string> extraArgs) =>
        Launch(MongodBinaries.Mongod, port, root, ["--wiredTigerCacheSizeGB", "0.25", .. extraArgs], dbPath: true);

    public static MongodProcess StartMongos(int port, string root, string configDb) =>
        Launch(Path.Combine(Path.GetDirectoryName(MongodBinaries.Mongod)!, "mongos"), port, root, ["--configdb", configDb], dbPath: false);

    private static MongodProcess Launch(string binary, int port, string root, IEnumerable<string> extraArgs, bool dbPath)
    {
        var dir = Path.Combine(root, $"db-{port}");
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        IEnumerable<string> args = ["--port", port.ToString(), "--bind_ip", "127.0.0.1", "--logpath", Path.Combine(dir, "mongod.log"),
            "--setParameter", "diagnosticDataCollectionEnabled=false"];
        if (dbPath) args = args.Concat(["--dbpath", dir]);
        foreach (var a in args.Concat(extraArgs))
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("mongod did not start");
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new MongodProcess(p, port, dir);
    }

    public async Task WaitListeningAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"mongod on {Port} exited with {_process.ExitCode}; see {DbPath}/mongod.log");
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await s.ConnectAsync(IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }
        throw new TimeoutException($"mongod on {Port} not listening after {timeout}");
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _process.Dispose();
    }
}

/// <summary>A real deployment for the conformance suite (06 §1): standalone or replica set, optionally TLS.</summary>
public sealed class Deployment : IAsyncDisposable
{
    public required string Kind { get; init; }
    public required IReadOnlyList<MongodProcess> Members { get; init; }
    public required string Root { get; init; }
    public string? SetName { get; init; }
    public TestCertificates? Tls { get; init; }

    /// <summary>Connection string straight to the deployment (bypassing the proxy).</summary>
    public string DirectUri
    {
        get
        {
            var hosts = string.Join(',', Members.Select(m => $"localhost:{m.Port}"));
            var opts = new List<string>();
            if (SetName != null) opts.Add("replicaSet=" + SetName);
            if (Tls != null) opts.Add("tls=true");
            return $"mongodb://{hosts}/" + (opts.Count > 0 ? "?" + string.Join('&', opts) : "");
        }
    }

    public static async Task<Deployment> StartStandaloneAsync(TestCertificates? tls = null)
    {
        MongodBinaries.SkipIfMissing();
        var root = NewRoot();
        var port = Ports.FreeRange(1);
        var m = MongodProcess.Start(port, root, TlsArgs(tls));
        await m.WaitListeningAsync(TimeSpan.FromSeconds(30));
        return new Deployment { Kind = "standalone", Members = [m], Root = root, Tls = tls };
    }

    public static async Task<Deployment> StartReplicaSetAsync(int members = 3, TestCertificates? tls = null, string setName = "rs0")
    {
        MongodBinaries.SkipIfMissing();
        var root = NewRoot();
        var basePort = Ports.FreeRange(members);
        var procs = Enumerable.Range(0, members)
            .Select(i => MongodProcess.Start(basePort + i, root, ["--replSet", setName, .. TlsArgs(tls)]))
            .ToList();
        foreach (var p in procs)
            await p.WaitListeningAsync(TimeSpan.FromSeconds(30));

        var d = new Deployment { Kind = "replicaSet", Members = procs, Root = root, SetName = setName, Tls = tls };
        var direct = d.NewClient(d.MemberUri(0, direct: true));
        var config = new BsonDocument
        {
            ["_id"] = setName,
            ["members"] = new BsonArray(procs.Select((p, i) => new BsonDocument
            {
                ["_id"] = i,
                ["host"] = $"localhost:{p.Port}",
                ["priority"] = i == 0 ? 2 : 1,
            }))
        };
        await direct.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("replSetInitiate", config));
        await d.WaitForPrimaryAsync(TimeSpan.FromSeconds(60));
        return d;
    }

    /// <summary>A client for this deployment that trusts its test CA.</summary>
    public MongoClient NewClient(string uri) => TestClients.Create(uri, Tls?.CaFile);

    public string MemberUri(int i, bool direct)
    {
        var tls = Tls != null ? "&tls=true" : "";
        return $"mongodb://localhost:{Members[i].Port}/?directConnection={(direct ? "true" : "false")}{tls}";
    }

    public async Task WaitForPrimaryAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            for (var i = 0; i < Members.Count; i++)
            {
                try
                {
                    var c = NewClient(MemberUri(i, direct: true));
                    var hello = await c.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
                    if (hello.GetValue("isWritablePrimary", false).ToBoolean())
                        return;
                }
                catch (Exception) { /* member not up yet */ }
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("no primary elected");
    }

    /// <summary>1-node config replica set, 1-node shard replica set, <paramref name="routers"/> mongos (06 §1).</summary>
    public static async Task<Deployment> StartShardedAsync(int routers = 2)
    {
        MongodBinaries.SkipIfMissing();
        var root = NewRoot();
        var ports = Ports.FreeRange(2 + routers);
        var cfg = MongodProcess.Start(ports, root, ["--configsvr", "--replSet", "cfg"]);
        var shard = MongodProcess.Start(ports + 1, root, ["--shardsvr", "--replSet", "sh0"]);
        await cfg.WaitListeningAsync(TimeSpan.FromSeconds(30));
        await shard.WaitListeningAsync(TimeSpan.FromSeconds(30));
        foreach (var (p, name) in new[] { (cfg, "cfg"), (shard, "sh0") })
        {
            var c = new MongoClient($"mongodb://localhost:{p.Port}/?directConnection=true");
            var conf = new BsonDocument { ["_id"] = name, ["members"] = new BsonArray { new BsonDocument { ["_id"] = 0, ["host"] = $"localhost:{p.Port}" } } };
            if (name == "cfg") conf["configsvr"] = true;
            await c.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("replSetInitiate", conf));
            await WaitWritable(c);
        }
        var mongos = Enumerable.Range(0, routers).Select(i => MongodProcess.StartMongos(ports + 2 + i, root, $"cfg/localhost:{cfg.Port}")).ToList();
        foreach (var m in mongos)
            await m.WaitListeningAsync(TimeSpan.FromSeconds(60));
        var router = new MongoClient($"mongodb://localhost:{mongos[0].Port}/");
        await router.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("addShard", $"sh0/localhost:{shard.Port}"));
        return new Deployment { Kind = "sharded", Members = mongos, Root = root, Backing = [cfg, shard] };
    }

    private static async Task WaitWritable(MongoClient c)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var hello = await c.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
            if (hello.GetValue("isWritablePrimary", false).ToBoolean()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("replica set did not elect");
    }

    /// <summary>Processes behind <see cref="Members"/> (config and shard servers of a sharded deployment).</summary>
    public IReadOnlyList<MongodProcess> Backing { get; init; } = [];

    private static IEnumerable<string> TlsArgs(TestCertificates? tls) => tls == null
        ? []
        : ["--tlsMode", "requireTLS", "--tlsCertificateKeyFile", tls.ServerPemFile, "--tlsCAFile", tls.CaFile, "--tlsAllowConnectionsWithoutCertificates"];

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "spyglass-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var m in Members.Concat(Backing))
            await m.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}
