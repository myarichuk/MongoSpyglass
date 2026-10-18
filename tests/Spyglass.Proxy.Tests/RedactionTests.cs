using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Spyglass.Proxy.Tests.Infrastructure;

namespace Spyglass.Proxy.Tests;

/// <summary>
/// 06 §2.6: authenticate through the proxy and grep everything the proxy can emit (wire records including
/// decoded command bodies, metrics, Trace-level logs) for the password, a secret field value and the lsid UUID.
/// Zero hits allowed.
/// </summary>
[Collection("console")] // captures Console.Error
public sealed class RedactionTests
{
    private const string Password = "Sup3r-Secret-Pa55word!";
    private const string Token = "tok_9f8e7d6c5b4a39281706";

    [Fact]
    public async Task Nothing_secret_leaves_the_proxy()
    {
        MongodBinaries.SkipIfMissing();
        var root = Path.Combine(Path.GetTempPath(), "spyglass-tests", "auth-" + Guid.NewGuid().ToString("N")[..8]);
        var port = Ports.FreeRange(1);
        await using var mongod = MongodProcess.Start(port, root, ["--auth"]);
        await mongod.WaitListeningAsync(TimeSpan.FromSeconds(30));

        var log = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(TextWriter.Synchronized(log));
        try
        {
            await using var proxy = await ProxyHost.StartAsync($"mongodb://localhost:{port}/", listeners: 1, configure: a => a["logLevel"] = "trace");

            // localhost exception: the proxy dials from 127.0.0.1, so the first user can be created through it
            var anon = new MongoClient(proxy.Uri());
            await anon.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument
            {
                ["createUser"] = "app",
                ["pwd"] = Password,
                ["roles"] = new BsonArray { new BsonDocument { ["role"] = "root", ["db"] = "admin" } },
            });

            Guid lsidUuid = default;
            foreach (var mechanism in new[] { "SCRAM-SHA-256", "SCRAM-SHA-1" })
            {
                var client = new MongoClient($"mongodb://app:{Uri.EscapeDataString(Password)}@localhost:{proxy.BasePort}/?authSource=admin&authMechanism={mechanism}");
                using var session = await client.StartSessionAsync();
                lsidUuid = session.ServerSession.Id["id"].AsGuid;
                var coll = client.GetDatabase("sec").GetCollection<BsonDocument>("users");
                await coll.InsertOneAsync(session, new BsonDocument { ["user"] = "u", ["password"] = Password, ["nested"] = new BsonDocument("apiKey", Token) });
                await coll.Find(session, new BsonDocument { ["password"] = Password }).ToListAsync();
                await coll.Find(session, new BsonDocument("nested.apiKey", Token)).ToListAsync(); // dotted path
                await coll.UpdateOneAsync(session, new BsonDocument("user", "u"), new BsonDocument("$set", new BsonDocument("nested.apiKey", Token)));
            }

            // change the password too: updateUser is redacted wholesale
            var root2 = new MongoClient($"mongodb://app:{Uri.EscapeDataString(Password)}@localhost:{proxy.BasePort}/?authSource=admin");
            await root2.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument { ["updateUser"] = "app", ["pwd"] = Password + "2" });

            await proxy.WaitRecordsAsync(r => r.GetProperty("type").GetString() == "op" && r.GetProperty("command").GetString() == "updateUser", 1);
            var metrics = await proxy.HttpGetAsync("/metrics");

            // ---- the haystack ----
            var haystack = new List<(string Where, byte[] Bytes)>();
            foreach (var r in proxy.Records)
            {
                var json = r.GetRawText();
                haystack.Add(("record " + json[..Math.Min(80, json.Length)], Encoding.UTF8.GetBytes(json)));
                if (r.TryGetProperty("cmd", out var cmd) && cmd.ValueKind == JsonValueKind.String)
                    haystack.Add(("cmd of " + r.GetProperty("command").GetString(), Convert.FromBase64String(cmd.GetString()!)));
            }
            haystack.Add(("metrics", Encoding.UTF8.GetBytes(metrics)));
            haystack.Add(("log", Encoding.UTF8.GetBytes(log.ToString())));

            var needles = new List<(string What, byte[] Bytes)>
            {
                ("password", Encoding.UTF8.GetBytes(Password)),
                ("token", Encoding.UTF8.GetBytes(Token)),
                ("lsid bytes", lsidUuid.ToByteArray(bigEndian: true)),
                ("lsid string", Encoding.UTF8.GetBytes(lsidUuid.ToString())),
                ("lsid hex", Encoding.UTF8.GetBytes(Convert.ToHexStringLower(lsidUuid.ToByteArray(bigEndian: true)))),
            };

            var hits = (from h in haystack from n in needles where h.Bytes.AsSpan().IndexOf(n.Bytes) >= 0 select $"{n.What} in {h.Where}").ToList();
            Assert.Empty(hits);

            // positive controls: the redaction markers are there
            var ops = proxy.RecordsOf("op").ToList();
            Assert.Contains(ops, o => o.GetProperty("command").GetString() == "saslStart" || o.GetProperty("command").GetString() == "saslContinue");
            var create = ops.Single(o => o.GetProperty("command").GetString() == "createUser");
            var createCmd = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(Convert.FromBase64String(create.GetProperty("cmd").GetString()!));
            Assert.Equal(new BsonDocument("$redacted", "createUser"), createCmd);
            Assert.True(log.ToString().Length > 0); // trace logging actually ran
        }
        finally
        {
            Console.SetError(originalErr);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
