# MongoSpyglass — code & architecture review

Date: 2026-10-17 · Scope: whole repo at `56dc041` · Reproduction harness: `tools/ReviewHarness`

Every finding below is either **[measured]** (reproduced by `tools/ReviewHarness`, see §1) or
**[read]** (established by reading the code; file:line given). Where I'm inferring library
behaviour I haven't executed, it says **[unverified]**.

---

## 0. Verdict

The wire-level skeleton (frame splitting, header parsing, arena-backed in-place BSON index)
is a reasonable start. Nearly everything stacked on top of it either doesn't work or works
against the 5–10% goal:

1. **The analyzer engine wedges itself on the first request.** One `find` sends
   `ISession.Fire()` into an unbounded loop. In a 15-second run with the real driver it
   produced **zero insights**, and its lock was still held 3 s later. Every rule-based feature
   (N+1, duplicates, slow queries, cursor leaks) is therefore non-functional. [measured]
2. **The "async observability" is synchronous.** Both listeners run on the proxy's socket
   read loop. The Rete listener alone costs **~64–79 µs per message** and throws **12
   first-chance exceptions per round trip**. [measured]
3. **The full stack cuts throughput by 37–42%, more than doubles 4 MB response latency, and
   triples p99**, measured against a fake server that is itself slow. Against a real mongod
   the relative cost will be worse. [measured]
4. **Several headline features are broken by construction:** zlib decompression, app-name
   attribution, killCursors tracking, and Settings-page saving. Ops/sec also tops out at
   1000. [measured / read]
5. **Every bounded queue between the proxy and storage is fake-bounded somewhere.** Memory is
   bounded by message count, not bytes; the Raven queue accumulates blocked writers without
   limit. [read]

The split you're considering (thin proxy + separate UI/collector) is the right call. It's
also the only way to hit the overhead budget, because almost all the current cost lives in
code that has no business being on the forwarding path. §9 has a concrete design.

---

## 1. Measurements

Setup: a real `MongoDB.Driver` 3.8 `MongoClient` talks to [Mongo.Fakes](https://github.com/myarichuk/Mongo.Fakes)
0.12.0, a wire-compatible in-process fake that runs as a separate process because of the
MongoDB.Bson 2.x/3.x split. Three variants:

* `direct`: client → fake
* `bare`: client → `MongoDbProxy` with no Service listeners → fake
* `full`: client → `MongoDbProxy` + `TrafficMonitorService` + `ReteAnalyzerEngine` (+ hub,
  metrics) → fake. RavenDB is *not* initialised, so storage costs are excluded. This is the
  best case for Spyglass.

The machine has 4 vCPUs. Numbers below are from 3 consecutive runs (ranges where they varied);
reproduce with `tools/ReviewHarness/run.sh e2e`.

| | direct | bare proxy | full stack |
|---|---|---|---|
| find by `_id`, sequential, p50 | 1176–1255 µs | 1353–1527 µs | 1276–1436 µs |
| same, p99 | 2.8–4.2 ms | 3.3–4.1 ms | **7.3–8.7 ms** |
| 32 concurrent workers, throughput | 3306–3395 ops/s | 2883–3097 ops/s (−7…−15%) | **1921–2126 ops/s (−37…−42%)** |
| `find({})` returning ~4 MB, p50 | 13.4–16.8 ms | 18.5–19.0 ms (+13…+38%) | **34.7–38.7 ms (+130…+160%)** |
| first-chance exceptions per find round trip | – | 0 | **12** |
| `ReteAnalyzerEngine.OnMessage` mean, on the I/O thread | – | – | 64–79 µs (max 38–62 ms) |
| `TrafficMonitorService.OnMessage` mean | – | – | 2.5–5 µs |
| insights produced in the whole run | – | – | **0** |
| engine lock acquirable within 3 s after the run | – | – | **no** |

**Caveat that makes this worse, not better:** the fake costs ~1.2 ms per find and caps at
~3.3K ops/s. That is 5–10× slower than a local mongod doing a point lookup. The proxy's
*absolute* cost (tens of µs per message for `bare`, ~150 µs per round trip for `full`) is
fixed, so against a 150–300 µs real mongod round trip the relative overhead roughly
quintuples.

Unit-level repros (`run.sh unit`):

```
A1  single request (shape!=value): rules fired=2000 (HIT CAP -> unbounded loop); windows=S:668,V:668
A1b single request (shape==value): rules fired=2000 (HIT CAP -> unbounded loop); windows=H:1501; insights=498
A2  after find response: open cursors=1
A2  after killCursors: fired=4 open cursors=1 (expected 0)
A2  after connection close: fired=2000 (HIT CAP -> unbounded loop) cursorFacts=0 stats.TotalClosed=668
A3  after 20 completed req/resp: RequestObserved=20 ResponseObserved=20 ...  (never retracted)
A4  threshold=500, response=200ms: PendingRequestFact left=1 (expected 0)
B   filter value regex     : command parse ok=True, filter subdoc parse ok=False
B   filter value javascript: command parse ok=True, filter subdoc parse ok=False
B   filter value undefined : command parse ok=True, filter subdoc parse ok=False
C   zlib payload (header 78 01) via DeflateStream (what proxy uses): THROWS InvalidDataException
D   8x PipeWriter.WriteAsync(200B) + 1 FlushAsync => underlying stream writes=8
D   8x PipeWriter.Write(200B) + 1 FlushAsync  => underlying stream writes=1
```

---

## 2. P0: correctness bugs that invalidate features

### 2.1 Rete rules re-fire on their own updates, so `Fire()` never returns [measured]

`TrackN1ShapeRule.UpdateN1Window` (`Analyzers/Rete/NPlusOneRule.cs:31-43`) mutates the
`RequestWindowFact` it matched and calls `ctx.Update(window)`. The match condition
`(tick.CurrentTime - w.LastSeen) < WindowMs` is still true afterwards, because `LastSeen` was
just set to `tick.CurrentTime`. NRules rules are repeatable by default, so the same activation
fires again, and so on forever. `TrackDuplicateQueryRule` (`:140-145`) has the same shape.
`DetectN1PatternRule` and `DetectDuplicateQueryRule` then re-fire on every one of those
updates and insert a new `Insight` each time (A1b: 498 insights from **one** request).

Consequence in the running service (`ReteAnalyzerEngine.cs:542-547`): the first `Fire()` that
sees a request never returns. It holds `_syncLock` forever, which in turn hangs:

* `OnConnectionClosed` (`ReteAnalyzerEngine.cs:459-466`), called on the proxy's connection
  task, so every connection close leaks a thread-pool thread;
* `ActiveCursorsCount` / `GetCursorStats`, called from **every render** of Operations,
  Analysis and CursorHistory, and from the Prometheus scrape (`MetricsService.cs:231`).
  That's a hung Blazor circuit and a hung `/metrics`.

Fix: don't express windowed counters as RETE rules at all (§6.4). If you keep NRules:
`[Repeatability(RuleRepeatability.NonRepeatable)]`, and make the update not re-satisfy the
condition.

### 2.2 Closed cursors are resurrected; connection close loops forever [measured]

`DetectNewCursorRule` (`CursorRules.cs:197-200`) is `ResponseObservedFact(cursorId>0) ∧
¬CursorFact(same id)`. `ResponseObservedFact` is never retracted (2.3), and
`UpdateCursorStatsRule` (`:240-248`) retracts a cursor as soon as it's closed. Retraction
re-satisfies the `Not`, so the cursor comes back as a fresh *open* `CursorFact` (A2:
killCursors leaves 1 open cursor).

On connection close, `ConnectionClosedRule` closes it again, stats retract it,
`DetectNewCursorRule` resurrects it, `ConnectionClosedFact` (also never retracted) matches
again… an unbounded loop (A2: `TotalClosed=668` from one cursor). Resurrected cursors that
avoid this loop become "Cursor Leak Detected" after 1 h idle, which is a guaranteed false
positive.

### 2.3 Ingest facts are never retracted [measured + read]

No rule retracts `RequestObservedFact`, `ResponseObservedFact`, `GetMoreRequestedFact`,
`ConnectionClosedFact` or `ConnectionAppInfoFact` (grep `Retract`; A3). Working memory grows
by every message for the lifetime of a session. Every `TimeTick` update (1 Hz) re-propagates
through every tick-dependent join over all of it. A3 shows 33–48 ms per tick with only 20
pairs resident. Even with 2.1 fixed, this is an O(messages) memory leak and an O(n·m) CPU leak.

`DetectSlowQueryRule` (`SlowQueryRules.cs:279-285`) also has no join condition between
`RequestObservedFact` and `ResponseObservedFact` before the `PendingRequestFact` node. It
materialises the **cartesian product** of all requests × all slow responses in beta memory.

### 2.4 Slow-query bookkeeping ignores the configured threshold [measured]

`CleanupPendingRequestRule` hardcodes `DurationMs <= 100` (`SlowQueryRules.cs:324`).

* With the threshold set to 500 ms, any response between 100 and 500 ms matches neither rule,
  and the `PendingRequestFact` leaks for 5 minutes (A4).
* With the threshold below 100, both rules match the same pending fact, and whichever fires
  first wins.

The dashboard's "slow" list hardcodes `> 100` too (`Pages/Index.razor:162`).

### 2.5 zlib decompression can never succeed [measured]

MongoDB's `zlib` compressor emits RFC 1950 streams (zlib header `78 xx`).
`TryDecompressMessage` uses `DeflateStream`, which reads raw RFC 1951 and throws on that
header (`MongoDbProxy.cs:528-536`, C). Use `ZLibStream`. As written, enabling
`compressors=zlib` makes every message invisible, and snappy/zstd are explicitly unimplemented
(`:520-550`). Most production drivers negotiate snappy or zstd, so today: **compression on
means profiler blind**. Options are in §9.6.

### 2.6 App attribution reads the wrong direction [read]

`ReteAnalyzerEngine.OnMessage` looks for `client.application.name` in the **response**
(`ReteAnalyzerEngine.cs:207-219`). The handshake `client` document is sent by the driver in
the *request* (`hello`/`isMaster`), and the server never echoes it. The feature
(`2fa5ca5 Week 3.3`) can't fire.

### 2.7 Settings page silently resets settings on save [read]

`Settings.razor:67` clones only 7 fields into a new `AppSettings`. `BindAddress`,
`CursorLeakAlertThresholdHours`, `N1DetectionWindowMs`, `N1CountThreshold` and
`HeadlessMode` revert to their defaults on every save. Concretely: if you set
`BindAddress=127.0.0.1` and then change the slow-query threshold, **the proxy rebinds to
0.0.0.0**.

Additionally, any save:

* restarts the listener and **kills all live client connections** (`MongoDbProxy.cs:57-67`,
  connections share `_runCts`), even for a pure threshold change;
* can kill the proxy permanently. `GetCurrentSettings()` (`int.Parse`, DNS) runs *outside*
  the `try` in `StartProxyInternal` (`MongoDbProxy.cs:81`), so a typo in `MongoDbUrl` throws
  into a fire-and-forget task and leaves no listener running;
* may throw from the analyzer's handler: `_session.Update(...)` on a fact that isn't
  inserted yet, before hydration or after a session switch (`ReteAnalyzerEngine.cs:67-78`).
  That aborts the multicast `OnSettingsChanged` before the proxy's handler runs, depending on
  subscription order;
* reports "Settings Saved" before the save happens (`SettingsService.Update` is
  fire-and-forget).

`RavenEmbedded`/`RavenRemoteUrl`/`RavenDatabase` are shown in the UI and persisted, but never
read: `Program.cs` calls `Initialize(dataDir:)` only. They can't be read, because settings
live *in* Raven.

### 2.8 Ops/sec can never exceed 1000 [read]

`CalculateThroughput` counts entries in the last second of the 1000-entry ring buffer
(`TrafficMonitorService.cs:123-151`), after noise filtering. For a tool whose job is "why
does this break under load", that counter saturates exactly when it matters. "Total
Operations" on the dashboard is also `traffic.Count`, which is the same 1000-capped buffer,
and it appears twice ("Captured Events" too).

### 2.9 Query shape / value hashing is wrong for the canonical N+1 [read]

`ComputeQueryHashes` (`ReteAnalyzerEngine.cs:286-350`) has four problems:

* It only inspects `find.filter`. For every other command it hashes the *command document's*
  top-level keys, including `lsid`, `$clusterTime`, `txnNumber`, `ordered`. Filters inside
  `updates[].q`, `deletes[].q`, `pipeline[0].$match`, and kind-1 sections are never looked at.
* `fieldOperators` is never populated, so `{a:{$gt:1}}` and `{a:{$in:[…]}}` share a shape.
  Only top-level keys count.
* Values are included only if they're strings (`GetString` in a try/catch per field). So
  `find({_id: ObjectId(..)})` and `find({userId: 42})` produce `valueHash == shapeHash`
  (identical input bytes). This is **exactly** the N+1 pattern (load-by-id in a loop): every
  such query is classified as "Repeated Identical Query — consider caching", and both rule
  families update the same window (A1b).
* A nested BSON regex, JavaScript or undefined value makes `GetDocument("filter")` return
  `default` (2.10). The filter then contributes nothing.

---

## 3. P0: the "async" observation path is synchronous

`ProcessPipeAsync` (`MongoDbProxy.cs:224-235`) does, per frame, on the socket read loop:

1. `WriteAsync` per segment, then `FlushAsync`. That's one stream write per segment (D), one
   flush per frame, and an extra memcpy into the writer's buffer.
2. `ObserveMessage`: rent an arena, **memcpy the entire frame** (up to 48 MB) into native
   memory (`:305-306`), decompress, build a hash index of the body, then call each listener
   **synchronously** (`:464-468`).
3. Listener 1, `TrafficMonitorService.OnMessage`: cheap. It `TryWrite`s to a channel.
4. Listener 2, `ReteAnalyzerEngine.OnMessage`: string allocation, LINQ, StringBuilders,
   XxHash, hex strings, and per-field `try { GetString } catch {}` / `try { GetInt32 } catch
   {}` cascades (`:356-373`, `:422-452`). Measured: 12 exceptions per round trip, ~64–79 µs
   per message.

Only after all of that does the loop read the next frame. The comment says
`// 2. Async Observability`; it isn't.

Other per-frame costs on the hot path:

* `_listeners.Any(l => l.NeedsFullDocument)` and `foreach` over `IEnumerable<ITrafficListener>`
  allocate and interface-dispatch per message (`:351`, `:464`). Materialise once.
* `tag == "from"` string compares, and `Tag`/`ConnectionId` strings in every
  `ObservedMessage`. Use a bool/enum and an `int`/`long` connection id.
* Interpolated-string logging (`$"..."` into `LogDebug/LogTrace/LogWarning`) allocates even
  when the level is disabled (`:166`, `:179`, `:247`, `:251`, `:281`, `:474`). Use
  `LoggerMessage` source-gen or structured templates.
* Store-and-forward: a frame isn't forwarded until it's fully buffered. For a 16 MB batch,
  the client's time to first byte is delayed by the whole transfer through the proxy. That's
  the 4 MB +38% in the `bare` column. Default `StreamPipeReader` buffer is 4 KB, so a 4 MB
  response is ~1000 reads and ~1000 writes.

### 3.1 Sockets: no `NoDelay` [read]

Neither the accepted socket nor the upstream `TcpClient` sets `NoDelay`
(`MongoDbProxy.cs:143`, `:168`). Drivers set `TCP_NODELAY` because Mongo is request/response
with small messages. Combined with one write per segment (D), a message that straddles a
segment boundary goes out as two `send`s. The second can sit behind Nagle waiting for the
peer's delayed ACK (~40 ms on Linux). That is a plausible source of the p99 tail.

### 3.2 Request/response timing races [measured]

The request timestamp is recorded **after** the request has been forwarded *and* flushed
*and* copied (`MongoDbProxy.cs:333-336`). The response is observed on the other pipe's task.
If the server answers before `RecordRequest` runs, the response has no timing:
`missing(null) on responses` = 13–86 per run. `CorrelationRingBuffer` is also shared between
the two tasks with no synchronisation. Its 16-byte struct slots can tear, and only the
`RequestId` check saves you most of the time.

Take the request timestamp at frame completion, *before* forwarding. Correlate on the
response side only, single-writer.

### 3.3 Invalid frame = connection hang + unbounded buffer [read]

If `length < 16 || length > 48 MB`, `TryReadMessage` returns `false` (`:279-284`) and the loop
does `AdvanceTo(buffer.Start, buffer.End)`: "I've examined everything, need more data". It
will never be able to parse that prefix, so it never forwards another byte and keeps
buffering the client's stream forever, logging a warning per read. A proxy should either
fail closed (drop the connection) or degrade to blind passthrough. It must never stall.

### 3.4 Smaller proxy issues [read]

* Target is an `IPEndPoint`, resolved once via `Dns.GetHostAddresses(...)[0]`
  (`SettingsService.cs:222-249`). It doesn't re-resolve, picks an arbitrary address family,
  and `Split(':')` breaks IPv6 literals. The setting is named `MongoDbUrl` but doesn't accept
  a `mongodb://` URL.
* `_retryPolicy` (Polly) is constructed and never used (`MongoDbProxy.cs:45-52`).
* `messageSize` decompression branch is dead code: `bodyLength` was already reassigned to
  `decompLen`, so `decompressedBodyLen != bodyLength` is always false (`:455-460`).
* OP_MSG section walk: if no kind-0 section is found, `metadataPtr` stays at the kind byte
  and gets parsed as BSON (`:376-399`).
* zlib path: `compressedSpan.ToArray()` plus a `MemoryStream(uncompressedSize)`, so two
  managed allocations of up to 48 MB each (LOH) per compressed message (`:528-536`).
* Every frame larger than 1 MB disposes its arena on release (`ArenaTracker.cs:17`), so
  native alloc/free churns for every large batch. 512 pooled arenas × up to 1 MB retained is
  512 MB of resident native memory.
* `ObserveMessage`'s catch calls `tracker.Release()` even when the exception came from a
  listener *after* `AddRef`s were handed out (`:472-476`). Ref-count ownership is fragile.
  A double release returns a live arena to the pool, which is a use-after-free on native
  memory. Nothing guards against negative counts.
* On connection close, completing one pipe's reader/writer disposes the shared
  `NetworkStream` (`leaveOpen` defaults to false). The other pipe then sees
  `ObjectDisposedException`, which isn't in the filtered catch, so every disconnect is likely
  logged as an **Error** (`:253-256`). [unverified at runtime]

---

## 4. P1: memory and backpressure

| Queue | Bound | Real behaviour |
|---|---|---|
| `TrafficMonitorService._incomingChannel` | 2048 messages, DropWrite | Each queued message pins its arena: up to 2048 × 48 MB of native memory. Bound it by **bytes**. |
| `TrafficMonitorService._circularBuffer` | 1000 entries | Each holds `RawBson` **and** `ResponseBson` as `byte[]` (LOH for >85 KB). 1000 × (request + 16 MB batch) means GBs. Count-bounded, not byte-bounded. |
| `_pendingRequests` | 5-min TTL | Requests with `moreToCome` (w:0) never get a reply; each pins its `byte[]` body for 5 minutes. |
| `RavenStorageService._bulkChannel` | 10 000, `FullMode=Wait` | Called fire-and-forget (`TrafficMonitorService.cs:219`). When full, each call parks a pending writer holding the op + `byte[]`. Parked writers are **unbounded**. |
| Raven attachment saves | none | `Task.Run` per 512-batch, fire-and-forget (`RavenStorageService.cs:306-325`). Concurrency is unbounded when Raven is slow. |
| Rete `_channel` | 2048, DropWrite | Fine, except the working memory behind it is unbounded (2.3). |

Also:

* The response-side update does a linear scan of the 1000-entry ring under the write lock,
  doing string compares, **per response** (`TrafficMonitorService.cs:225-238`). Keep a
  reference to the entry in `_pendingRequests` instead.
* `NotificationHubService.Refresh()` runs **per request** (`TrafficMonitorService.cs:363`).
  It does `SelectMany` over all insights plus `_notifications.Any(...)` for each, which is
  O(100×100) per request on the consumer thread. It also runs every 2 s per open browser tab
  (`TopAppBar.razor`).
* Duration in `TrafficMonitorService` is `DateTime.Now - pending.Op.Timestamp`
  (`TrafficMonitorService.cs:203`), where `Timestamp` is when the *consumer dequeued* the
  request. It ignores the Stopwatch `msg.DurationMs` the proxy computed. Measured: the mean is
  close when the consumer is idle, but individual values are wrong (minimum **9–17 µs** for a
  >1 ms round trip). It degrades with backlog, since both messages get dequeued back-to-back.
  This is the number that feeds the UI, Raven, the map-reduce stats and the Prometheus
  histogram. It's local time (`DateTime.Now`) too, so DST jumps break it.
* `MongoOperation.SessionId` is stamped at bulk-flush time, not capture time
  (`RavenStorageService.cs:295`). Operations captured before a session switch land in the
  new session.

---

## 5. P1: BSON layer

* `TrySkipElement` has no cases for `0x06` undefined, `0x0B` **regex**, `0x0C` DBPointer or
  `0x0D` **JavaScript** (`ArenaBsonReader.cs:117-180`). It returns false, so the containing
  document becomes `default` (B). Regex is common in filters. It's harmless at the top level
  only because subdocuments are skipped by length prefix.
* `ReadInPlace` builds two `ArenaDictionary`s per document: an index **and a `keyCache` that
  can never hit**, because keys within one document are unique (`:35-58`). It also clones
  every key into the arena. For "get me the first key, `collection`, `cursor.id`" a linear
  scan over a ~10-element command document is cheaper than hashing all keys twice.
* Duplicate keys (legal BSON) make `index.Add` throw, which is caught, and the whole doc
  becomes `default`.
* `GetInt32` silently truncates `Int64` and `Double` (`BlittableBsonDocument.cs:377-387`).
  `BuildExampleJson` tries `GetInt32` before `GetInt64`, so every long in an example is
  truncated (`ReteAnalyzerEngine.cs:433-436`).
* `GetDataPointerAtOffset` returns `null` on malformed input and every caller dereferences it
  (`:597-618`), which relies on NRE plus a catch-all.
* `EscapeJsonString` doesn't escape `\` or control characters, so it emits invalid JSON
  (`ReteAnalyzerEngine.cs:454-457`).
* The proxy depends on `MongoDB.Bson` 2.19.1 (2023) just for `ObjectId`/`Decimal128` and a
  fallback writer path. It also pulls in **Roslyn** (`Microsoft.CodeAnalysis.CSharp`),
  Autofac, Polly, Stateless and RecyclableMemoryStream, most of them unused there, and with
  floating `Version="*"` (non-reproducible builds). For a "lightweight sidecar" the proxy
  csproj should have roughly zero dependencies.

---

## 6. P1: analyzer design

### 6.1 Rule engine is the wrong tool

What's being computed is stream aggregation: per-connection cursor state machines, per-shape
sliding-window counters, and latency thresholds. Each of those is O(1) per event with a
dictionary. RETE buys incremental pattern matching across many interacting facts, and here
it costs:

* reactivation semantics you have to fight (2.1);
* fact lifecycle you have to manage manually (2.2, 2.3);
* `Not` / cross-product joins with no hash indexing, so the cost per insert scales with
  working-memory size;
* a global lock shared by the engine thread, proxy threads and UI renders.

The Hibernating Rhinos profilers didn't need inference either: each alert is a local check
on a statement, a session (unit of work), or a short window.

### 6.2 What a straight-line analyzer looks like

* `Dictionary<(conn, cursorId), CursorState>`. A `find`/`aggregate` reply with `id != 0`
  opens a cursor. `getMore` touches it. `id == 0`, `killCursors` or connection close closes
  it. An idle scan every N seconds raises a leak alert **once** per cursor.
* `Dictionary<(conn or lsid or appName, shapeHash), Window>` with a ring of timestamps or a
  count+first-seen. Emit an N+1 when count ≥ K within W *and* distinct value hashes > 1, and
  a duplicate when count ≥ K with one value hash. Emit once per window, then re-arm.
* Slow query: `duration > threshold` at response time. No pending fact is needed, because
  the proxy already correlated it.

### 6.3 Shape normalisation

Canonicalise recursively: replace leaf values with their BSON type tag, keep operators, keep
`sort`/`projection`/`limit` presence, and descend into `updates[].q`, `deletes[].q` and the
`pipeline` stages. This is what MongoDB 8.0's own query-shape hash (`$queryStats`,
`queryShapeHash`) does. Align with it, so your hashes can be compared with the server's.

### 6.4 Attribution

* App: the `client.application.name` from the **request** handshake (2.6), stored per
  connection.
* Logical session: `lsid` is in every command from modern drivers. It's an implicit pooled
  session, so it doesn't equal a web request, but it's per-operation-chain.
* Unit of work: what HR profilers call a "session". Mongo has no free hook for it; the
  standard trick is the `comment` field. Have the app set `comment: {traceId}` via a driver
  interceptor, or carry W3C `traceparent` in `comment`. That gives you "requests that issued
  50 finds", which is the real N+1 detector. Per-connection windows can't distinguish one
  request doing 50 lookups from 50 requests doing one each over a pooled connection.

---

## 7. P2: security

* **No authentication** on the UI or `/metrics`, and `BindAddress` defaults to `0.0.0.0`
  (and 2.7 can silently restore that). The UI shows full query and response payloads.
* **Credentials are captured and persisted.** `saslStart`/`saslContinue`/`authenticate` are
  recorded verbatim. For `PLAIN` (LDAP), `saslStart.payload` is `\0user\0password` in clear.
  `createUser`/`updateUser` carry `pwd` in clear. All of it goes to the ring buffer, RavenDB
  attachments and exports. Redact these commands at capture time, in the proxy.
* The SCRAM conversation itself is safe to forward unmodified, and you do.
* Export: the smuggler `TransformScript` is now injection-safe by regex. It is also probably
  not filtering at all, for two reasons [unverified]. Raven transform scripts skip a document
  via `throw 'skip'`, not `return`. And the identity property is not in the document body:
  `this.Id` is undefined, so use `id(this)`. Check the exported file. The download is the
  whole export base64-encoded into one JS interop call and a `data:` URL; use an HTTP
  endpoint. The file is also a `.ravendbdump`, not `.json`.
* `_Host.cshtml` and `TopAppBar.razor` load Google Fonts, Material Symbols and an avatar from
  `lh3.googleusercontent.com`. That leaks every UI open to Google, and the commit
  "air-gapped deployments" doesn't hold: offline, every icon renders as its ligature text
  (`settings`, `search`, …).

---

## 8. UI (Blazor Server)

Works: it renders, the operations feed updates, and the operation-detail BSON tree is decent.

Broken or misleading:

* **Analysis → click a "slowest group" row crashes the circuit.** `SampleInstance` is always
  `null` (`Analysis.razor:68`), so `OperationDetail.OnParametersSet` NREs on `Entry.RawBson`.
  The build already warns about it (CS8604).
* Analysis re-queries the Raven map-reduce index **on every traffic tick (10 Hz) per open
  tab** (`Analysis.razor:216`, `async void`, mutates component state off the dispatcher).
* `GetCursorStats()` returns the *live* `CursorStatsFact` from inside the lock. The page then
  enumerates its `Queue<double>` (`AverageOpenTimeMs`) outside the lock while the engine
  mutates it, which gives "Collection was modified" during render and kills the circuit
  (`Analysis.razor:34`, `CursorHistory.razor`).
* Any render that touches `ActiveCursorsCount` blocks on the engine lock, so it **hangs
  forever** given 2.1 (`Operations.razor:25`, Analysis, CursorHistory).
* `<Virtualize>` inside `<tbody>` without `SpacerElement="tr"` (`Operations.razor:58`). The
  spacers render as `<div>` in a table body, which is invalid DOM and causes scroll jumps.
* Each row renders `SummaryJson`, which is the **entire** request body as JSON (an insert of
  10K docs is MBs). The cell truncates with CSS, but the full string goes through the
  SignalR render batch. Precompute a bounded summary at capture time.
* Nav links to `/statistics`, but there is no such page. `/cursors` exists but isn't linked.
  Documentation, Support, Help, the search box and "Copy ID" are dead controls.
* Dashboard: "Total Operations" and "Captured Events" are the same number, and both are
  capped at 1000. "Latency Distribution" is the last 20 points as a line. The slow threshold
  is hardcoded to 100.
* Timezones: `CursorFact` times are UTC. CursorHistory filters with local `DateTime` from
  `<input type=date>` (midnight), so the end date excludes the end day and the results are
  offset by the UTC delta.
* Every component subscribes to `OnTrafficReceived`, which fires from a `System.Timers`
  thread at 10 Hz. `async void` handlers recompute LINQ over the snapshot per tab.
* Leftovers: `WeatherForecast*.cs`, `UnitTest1.cs`, the `Logs/` folder item, the
  `.idea/…docker-compose.generated.override.yml` committed to the repo, and an empty
  `StateMachine/` folder in the proxy csproj.

For a profiler UI you'd want: a read-only client of the collector's API, served as static
files (Blazor WASM, or anything), instead of a Blazor Server circuit holding references into
live engine state. That's the split in §9.

---

## 9. Target architecture: proxy + collector/UI split

Goal: proxy overhead ≤ 5–10%, and **never** let observation slow down or break forwarding.

### 9.1 Be honest about the physics

On loopback, an extra hop costs two `recv` + two `send` + two scheduler wakeups per
direction, roughly 15–40 µs per round trip. A local mongod point lookup is ~150–300 µs, so
the hop alone is 5–20% for the cheapest operations before you parse a byte. Over a real
network (app ↔ db 0.5–1 ms RTT) it's noise. Pick your benchmark accordingly, and report
absolute µs/op added, not just percentages.

### 9.2 Proxy (sidecar container)

* **Cut-through forwarding.** Forward bytes as they arrive. Track frame boundaries with a
  tiny state machine: 4-byte length, then header, then countdown. Don't wait for the full
  frame before writing it on. Write with a single `Write`/`Flush` per read batch, or
  `Socket.SendAsync(IList<ArraySegment>)`. Set `NoDelay` on both sockets, and use
  64–256 KB read buffers.
* **Observation is a bounded copy.** At frame start, copy *at most* N bytes (say 16–64 KB)
  of the body into a per-connection observation buffer, alongside fixed-size metadata:
  conn id (int), requestId, responseTo, opcode, flags, wire length, `t_firstByte` and
  `t_lastByte` (Stopwatch ticks). Hand it to the observer via an SPSC ring and drop on full
  with a counter. **No listener code runs on the forwarding task.**
* **Cheap facts the proxy should extract itself,** because they need the full frame or are
  trivially cheap:
  * `nReturned`: walk the `firstBatch`/`nextBatch` element length prefixes in place on the
    `ReadOnlySequence` (a `SequenceReader`, no copy). That's O(docs) pointer hops.
  * `cursor.id`.
  * `ok` / `code` / `errmsg`.
  * The `writeErrors` count.
* **Redaction at the source:** never copy the bodies of `sasl*`/`authenticate`/`*User`.
* **Export:** length-prefixed binary records over a Unix socket or TCP to the collector,
  with no serialization library. Optionally, expose OTLP/Prometheus metrics (histograms per
  `command × namespace × app`) directly from the proxy, so you get telemetry even with no
  collector running. At work that alone answers "how much load until it breaks".
* **Dependencies:** BCL only.

### 9.3 Collector + UI (separate container)

* It consumes the record stream, and does the BSON decoding, shape hashing (§6.3), the
  analyzers (§6.2) and storage.
* **Storage:** stop writing one document plus one attachment per operation. Store:
  * per-(shape, minute) aggregates: count, HDR/DDSketch latency, bytes, docs returned. Raven
    **time series** fits this nicely if you want to stay in Raven;
  * K slowest exemplars plus a reservoir sample per shape, with the bounded body copy;
  * alerts, deduplicated by (rule, key) with occurrence counts, instead of one document per
    firing.
  * Raw per-operation capture only in an explicit "record session" mode, with a byte budget.
* The UI reads the collector's HTTP API. It shares no in-memory state with the analyzer, so
  the analyzer can't be locked by a render.

### 9.4 Topology: the thing that will bite you first at work

* **Replica sets.** The driver discovers members from `hello.hosts`/`primary`/`me` and
  connects to them **directly**, bypassing the proxy after the first handshake, unless the
  client uses `directConnection=true` (single node only). Either rewrite those fields in
  `hello` responses to proxy addresses (one listener per member), or document that only
  single-node/`directConnection` or mongos is supported.
* **`mongodb+srv://`** resolves to real hosts via DNS. Same problem.
* **TLS** (Atlas and most prod): the proxy is blind and currently not even able to sit in
  the path. You need TLS termination: client→proxy plaintext or proxy cert, proxy→server TLS.
  Decide this before writing more analyzers.

### 9.5 Zero-proxy alternative worth knowing about

For the immediate work problem (a data layer with no telemetry), the C# driver already
exposes command monitoring in-process: `ClusterConfigurator.Subscribe<CommandStartedEvent /
CommandSucceededEvent / CommandFailedEvent>`. There's also connection-pool events and OTel
`DiagnosticSource` integrations. That's command name, namespace, duration, reply and server
connection id at ~µs overhead, without any of the topology/TLS/compression problems above.

The proxy's value is that it's **app-agnostic** (no code change, any language, any driver).
That's a legitimate goal, but it's the expensive way to get the same numbers. Server-side
there's also the database profiler, slow-op log, and `$queryStats` (8.0+).

### 9.6 Compression

Pick one:

1. Implement snappy/zstd/zlib decode on the collector side. Snappier and ZstdSharp are pure
   managed. The proxy forwards compressed bytes untouched.
2. Strip `compression` from the client's `hello` so the hop is uncompressed. This is cheapest
   for observation, but changes app↔db bytes on the network.

The proxy should never decompress on the forwarding path.

---

## 10. Tests

* `ProxyIntegrationTests.Proxy_ShouldForwardTraffic_AndTriggerListener` asserts nothing, and
  leaks a `TcpListener`. `CursorRulesTests.KillCursorsRule_ClosesCursor` is an empty body
  with planning comments. Both pass, so 2 of the 30 tests are false confidence. The rule
  tests that exist insert hand-built facts. None of them inserts a `RequestObservedFact` and
  calls `Fire()`, which is why 2.1 was never noticed.
* No test exercises a real driver end to end. `tools/ReviewHarness` shows that Mongo.Fakes
  makes that cheap. Promote it to a proper integration test project that asserts:
  * forwarding works;
  * durations are within ε of client-observed;
  * insights are produced for an N+1 loop;
  * `ActiveCursorsCount` returns within a timeout;
  * the proxy's added µs/op stays under a budget (as a perf regression gate).

---

## 11. What's fine and worth keeping

* Frame splitting on `ReadOnlySequence` with a length check, and forwarding the original bytes
  unmodified.
* The in-place BSON idea: index offsets over the original buffer instead of materialising
  `BsonDocument`. Narrow it to "scan for the few keys I need".
* The pooled ref-counted arena, as a concept. With bounded copies it becomes a per-connection
  fixed buffer and the ref-counting mostly disappears.
* Using bounded `Channel`s with `DropWrite` between stages, as a direction. Apply it
  everywhere, by bytes.
* The `BsonUtils` OP_MSG section parsing for display, including kind-1 sequences.
* The UI's general information architecture: feed, detail with request/response tabs,
  insights list. The look is fine; the problems are all in data flow.

---

## 12. Suggested order of work

1. **Stop the bleeding (small, independent):**
   * move both listeners off the I/O loop;
   * set `NoDelay`;
   * one flush per read batch;
   * take the request timestamp before forwarding;
   * fail closed on invalid frames;
   * `ZLibStream`;
   * fix `Settings.razor` cloning, and don't restart the listener for non-network settings;
   * redact auth commands;
   * fix the `SampleInstance` NRE;
   * add the missing BSON types.
2. **Replace the Rete engine** with the straight-line analyzers in §6.2, and fix shape
   hashing (§6.3). This deletes ~800 lines and every P0 in §2.1–2.4.
3. **Make the proxy standalone:** BCL-only project, binary record export, Prometheus/OTLP
   metrics with command/namespace/app labels.
4. **Collector/UI container:** aggregates + exemplars storage, HTTP API, UI as an API client.
5. **Topology and TLS** (§9.4) before anyone points it at a real cluster.
6. Promote `tools/ReviewHarness` into CI as the end-to-end and perf-budget gate.
