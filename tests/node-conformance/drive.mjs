// node drive.mjs <driver package> <connection string> [appName]
// Runs a fixed workload and prints one JSON line per observation on stdout:
//   {"kind":"cmd", address, serverConnectionId, requestId, commandName, databaseName}
//   {"kind":"heartbeat", address, spyglass}            (reply.$spyglass of serverHeartbeatSucceeded)
//   {"kind":"servers", addresses}                      (topology description at the end)
//   {"kind":"done", inserted, found}
const [, , pkg, uri, appName = "node-conformance"] = process.argv;
const { MongoClient } = await import(pkg);

const out = (o) => process.stdout.write(JSON.stringify(o, (_, v) => (typeof v === "bigint" ? Number(v) : v)) + "\n");

const client = new MongoClient(uri, { appName, monitorCommands: true, serverSelectionTimeoutMS: 10000, heartbeatFrequencyMS: 500 });
if (typeof client.appendMetadata === "function") client.appendMetadata({ name: "spyglass-node", version: "0.0.0-test" });
client.on("commandStarted", (e) =>
  out({ kind: "cmd", address: e.address, serverConnectionId: e.serverConnectionId ?? null, requestId: e.requestId, commandName: e.commandName, databaseName: e.databaseName }));
client.on("serverHeartbeatSucceeded", (e) => out({ kind: "heartbeat", address: e.connectionId, spyglass: e.reply?.$spyglass ?? null }));

await client.connect();
const coll = client.db("nodeconf").collection("c" + Date.now().toString(36) + Math.floor(Math.random() * 1e6));
const docs = Array.from({ length: 250 }, (_, i) => ({ i, s: "x".repeat(i % 50) }));
const ins = await coll.insertMany(docs);
for (let i = 0; i < 10; i++) await coll.findOne({ i });
const all = await coll.find({}).sort({ i: 1 }).batchSize(40).toArray();
await coll.updateMany({ i: { $lt: 10 } }, { $set: { u: true } });
await coll.countDocuments({}, { readPreference: "secondaryPreferred" });
await coll.deleteMany({ i: { $gt: 240 } });
try { await client.db("nodeconf").command({ noSuchCommandNode: 1 }); } catch { /* expected */ }
// let at least one heartbeat land after connect
await new Promise((r) => setTimeout(r, 1500));
const servers = [...client.topology?.description?.servers?.keys?.() ?? []];
out({ kind: "servers", addresses: servers });
out({ kind: "done", inserted: ins.insertedCount, found: all.length });
await client.close();
