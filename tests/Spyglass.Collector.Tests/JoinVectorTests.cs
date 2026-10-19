using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spyglass.Collector.Model;
using Spyglass.Collector.Storage;

namespace Spyglass.Collector.Tests;

/// <summary>Where the 06 §3 conformance vectors live: <c>docs/spec/vectors/ingest-v0</c>.</summary>
internal static class Vectors
{
    public static string Root
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var v = Path.Combine(d.FullName, "docs", "spec", "vectors", "ingest-v0");
                if (Directory.Exists(v)) return v;
            }
            throw new DirectoryNotFoundException("docs/spec/vectors/ingest-v0 not found above " + AppContext.BaseDirectory);
        }
    }

    public static TheoryData<string> Files(string dir, string pattern) =>
        new(Directory.GetFiles(Path.Combine(Root, dir), pattern).Select(Path.GetFileName).Order().Select(f => f!));
}

/// <summary>
/// 06 §3 <c>join/*.json</c>: each scenario's records are ingested in order, each at its arrival time on a fake
/// clock, every stage drained after each; then the clock runs past every window. The stored operations must be
/// exactly the expected ones, joined to exactly the expected cmd; the listed 02 §6 counters must match.
/// </summary>
public sealed class JoinVectorTests
{
    private const long T = 1700000000000000;

    public static TheoryData<string> Scenarios => Vectors.Files("join", "*.json");

    [Theory]
    [MemberData(nameof(Scenarios))]
    public Task Scenario(string file) => Run(file, new MemoryStore());

    /// <summary>The same against SQLite: a late cmd is an update of a stored row there, not of an object in memory.</summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Scenario_sqlite(string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "spyglass-vectors-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var store = new SqliteStore(dir);
            await Run(file, store);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static async Task Run<TStore>(string file, TStore store) where TStore : IOperationStore, IAggregateStore, IAlertStore, ICallsiteStore
    {
        var doc = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Vectors.Root, "join", file)))!;
        var now = T;
        await using var c = new Collector(new CollectorOptions { IngestInsecure = true, Auth = AuthMode.None, Data = null, Workers = 3 },
            store, store, store, () => Volatile.Read(ref now));
        c.Start();

        foreach (var step in doc["input"]!.AsArray())
        {
            Volatile.Write(ref now, T + step!["at_ms"]!.GetValue<long>() * 1000);
            var line = step["record"]!.ToJsonString();
            var r = c.Ingest(Encoding.UTF8.GetBytes(line + "\n"), null, null);
            Assert.True(r is { Status: 204, Rejected: 0, Accepted: 1 }, $"{file}: {line} → {r}");
            await c.DrainAsync(TimeSpan.FromSeconds(10));
        }
        Volatile.Write(ref now, now + 60_000_000); // past every window: waiting ops are stored, waiting cmds orphaned
        await c.DrainAsync(TimeSpan.FromSeconds(10));

        var stored = store.Query(new OpQuery { Limit = 1000 });
        var expected = doc["expected"]!["ops"]!.AsArray();
        Assert.Equal(expected.Count, stored.Count);
        foreach (var e in expected)
        {
            var key = new JoinKey(e!["proxyId"]!.GetValue<string>(), e["member"]!.GetValue<string>(),
                e["serverConnectionId"]!.GetValue<long>(), e["requestId"]!.GetValue<int>());
            var op = Assert.Single(stored, o => o.Key == key);
            if (e["joined"] is not { } j)
                Assert.True(op.Ctx == null, $"{file}: {key} should be wire-only, joined {op.Ctx?.Src}/{op.Ctx?.OperationId}");
            else
            {
                Assert.True(op.Ctx != null, $"{file}: {key} should be joined");
                Assert.Equal(j["src"]!.GetValue<string>(), op.Ctx!.Src);
                Assert.Equal(j["operationId"]!.GetValue<string>(), op.Ctx.OperationId);
            }
            if (e["replyCount"] is { } rc) Assert.Equal(rc.GetValue<int>(), op.ReplyCount);
        }

        if (doc["expected"]!["counters"] is JsonObject counters)
            foreach (var (name, value) in counters)
                Assert.True(Counter(c.Join, name) == value!.GetValue<long>(), $"{file}: {name} = {Counter(c.Join, name)}, expected {value}");
        Assert.Equal(0, c.Join.PendingOps);
    }

    private static long Counter(Pipeline.JoinCounters j, string name) => name switch
    {
        "joined" => j.Joined,
        "joined_late" => j.JoinedLate,
        "conflict" => j.Conflicts,
        "orphan_no_key" => j.OrphanNoKey,
        "orphan_expired" => j.OrphanExpired,
        "late_discarded" => j.LateDiscarded,
        "already_joined" => j.AlreadyJoined,
        "duplicate" => j.Duplicates,
        _ => throw new ArgumentException($"unknown counter '{name}'"),
    };
}
