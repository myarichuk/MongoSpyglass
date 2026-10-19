# 02 — Spyglass Ingest Protocol v0

The protocol has two producer roles and one consumer:

* **proxy**: produces *wire records* (what happened on the wire);
* **exporter**: produces *context records* (what the application knew);
* **collector**: consumes both, and joins context onto wire facts.

Design rules:

* An exporter never needs configuration when it runs behind a proxy (§2).
* The join needs nothing from the wire that a driver doesn't already expose through the
  standard Command Monitoring and CMAP events (§3).
* Every record may be lost without affecting correctness elsewhere (I4, I5).
* Exporters send context, never content (I6).

## 1. Versioning

* The protocol version is in the URL path: `/ingest/v0`.
* Within v0, changes are **additive only**: new optional fields, new record types.
* Consumers MUST ignore unknown fields and unknown record `type`s. Producers MUST NOT rely
  on a consumer understanding a field added after the `hello` record's `schema` value.
* `schema` is a monotonically increasing integer within v0, starting at `1`.
* A breaking change gets a new path, `/ingest/v1`, served side by side for at least one
  collector release.
* Schema `1` is **frozen** as of M2, with the conformance vectors (§7). It was edited in place
  until then: the wire-record trim (absent fields, derived fields, no `proxyId` in wire records)
  and the fields M2 found missing from this text (`reply.cursorsKilled`/`cursorsNotFound`,
  `stats.dropped.pending_evicted`) were made under that rule. Anything after this is additive.

## 2. Discovery and the handshake marker

### 2.1 Overview

```
exporter                         proxy                              collector
   │  (driver connects to proxy listener)                              │
   │── hello  client.driver.name="nodejs|spyglass-node" ─▶│  R3?        │
   │◀─ hello reply + $spyglass{ingest,token,proxyId,...} ─│  R1,R2      │
   │  heartbeat event exposes reply → exporter activates               │
   │── POST /ingest/v0 (context records) ─────────────────────────────▶│
```

### 2.2 The `$spyglass` discovery document (rewrite R2)

The proxy appends this as the last top-level element of every hello-family reply:

```js
"$spyglass": {
  "v": 0,                                  // protocol major
  "schema": 1,                             // highest schema the proxy emits
  "proxyId": "8f0c2c1e-…",                 // UUID, new per proxy process start
  "member": "localhost:27017",             // advertised address of this listener (normalized, §3.2)
  "ingest": "http://spyglass-collector:4318/ingest/v0",   // may be absent (headless proxy)
  "token": "…"                             // present iff discovery.advertiseToken
}
```

Drivers ignore unknown hello reply fields. That's verified for the .NET driver 3.12 and the
Node driver 7.7 by the conformance suite (06 §2.3). Server-generated reply fields never
start with `$spyglass`.

### 2.3 Activation rules for exporters

An exporter is **dormant** until one of these holds:

1. It has observed a `$spyglass` document with `v == 0` and an `ingest` URL, in a
   `ServerHeartbeatSucceededEvent.reply` (both drivers expose the full hello reply on this
   event; verified in .NET driver 3.12 and Node driver 7.7).
2. It was explicitly configured with an ingest endpoint (`SPYGLASS_INGEST` or options).

And this does **not** hold:

3. It was explicitly disabled (`SPYGLASS_DISABLED=1` or options).

While dormant, an exporter MUST NOT allocate per command, MUST NOT buffer, and MUST NOT open
sockets. Its event handlers return after a single volatile read.

Explicit configuration overrides discovered values. If the discovered `ingest` changes (for
example, proxy restart), the exporter switches to the new endpoint for subsequent batches.

**Token trust.** The token is handed to any client that can open a connection to the proxy.
That client could already read the database through it, so the token only protects the
collector from *unrelated* network peers. Set `discovery.advertiseToken=false` and configure
exporters explicitly where that isn't acceptable.

### 2.4 Handshake marker

Exporters MUST add driver-info metadata to the client handshake, so the proxy (and the
collector) know which connections have an exporter and are worth waiting for:

| Driver | Mechanism | Resulting `client.driver.name` |
|---|---|---|
| .NET | `MongoClientSettings.LibraryInfo = new LibraryInfo("spyglass-dotnet", ver)`. If `LibraryInfo` is already set (ODM), compose `"<existing.Name>\|spyglass-dotnet"` with version `"<existing.Version>\|<ver>"`. | `mongo-csharp-driver\|spyglass-dotnet` |
| Node | `client.appendMetadata({ name: "spyglass-node", version })`. It applies to connections created after the call, so call it before `connect()` (05 §3). | `nodejs\|spyglass-node` |

* The proxy splits `driver.name` and `driver.version` on `|`. A token matching
  `^spyglass-[a-z0-9-]+$` sets the connection's `exporter` field (§4.2 `conn`).
* Drivers cap the handshake at 512 bytes and truncate `env`/`os` before `driver`, so the
  marker survives. If it's missing anyway, the only effect is a shorter join wait (03 §3.2).

## 3. The join key

### 3.1 Definition

```
joinKey = (proxyId, member, serverConnectionId, requestId)
```

| Field | Proxy knows it from | Exporter knows it from |
|---|---|---|
| `proxyId` | itself | `$spyglass.proxyId` seen in the heartbeat reply for the server at `member` |
| `member` | listener's advertised address | the `$spyglass.member` of the listener the command went to, mapped from the event's server address per §3.2 |
| `serverConnectionId` | `connectionId` in the hello reply on that upstream connection | .NET `ConnectionId.LongServerValue`; Node `event.serverConnectionId` (bigint, driver ≥ 6.4.0) |
| `requestId` | wire header of the request frame | .NET `CommandStartedEvent.RequestId`; Node `event.requestId` (the same value the driver writes into the wire header) |

**Uniqueness.**

* `serverConnectionId` is unique per member *process lifetime*.
* I3 guarantees the client-visible connection and the upstream connection are the same
  logical pair.
* `proxyId` changes on every proxy restart. `requestId` is unique per driver process for
  2³¹ requests.

A collision needs a member restart plus a reused connectionId plus the same requestId inside
the join window (03 §3.2). That's accepted as negligible, and any resulting mismatch is
detected (§6).

### 3.2 Address normalization

Both sides MUST normalize `member` identically:

* host lower-cased;
* IPv6 literal in brackets, compressed form;
* port always present, in decimal;
* no trailing dot.

Example: `LocalHost:27017` → `localhost:27017`; `::1` port 27018 → `[::1]:27018`.

Exporters do not send their own spelling of the address: they send the proxy's, the `member`
of the `$spyglass` document seen in the heartbeat for that server. The proxy and the collector
therefore compare strings the proxy produced. Mapping a command event to its server:

* **.NET:** `CommandStartedEvent.ConnectionId.ServerId` is the dialled endpoint (a `DnsEndPoint`
  carrying the host from the connection string or rewritten `hosts`) and is the same `ServerId`
  as the heartbeat's. Direct lookup.
* **Node:** `commandStarted.address` is **not** the dialled address. It is the socket's resolved
  remote `IP:port` (`streamIdentifier()` in `lib/cmap/connection.js`; verified in 6.4.0, 6.21.0
  and 7.7.0, where a client dialling `localhost:P` reports `127.0.0.1:P`). Heartbeat and CMAP
  events do carry the dialled host. The exporter maps by port: one proxy's listeners have
  distinct ports, so the port selects the listener whenever the client talks to a single proxy
  host. If two known listeners share the port (two proxies on different hosts), the exporter
  resolves both hostnames (`dns.lookup`, all addresses, cached per heartbeat) and matches the IP;
  if that is still ambiguous it sends `proxyId: null` (§3.3).

Verified end-to-end by the conformance suite (06 §2): with these rules every non-handshake
command event of the .NET driver 3.12 and the Node driver 6.4.0 / 6.21.0 / 7.7.0 matched exactly
one proxy `op` record on a 3-member replica set.

### 3.3 When an exporter can't build the key

* Handshake commands: the .NET driver raises `CommandStartedEvent` for the connection handshake
  (`isMaster`/`hello`) before the server has assigned the connection id, so
  `ConnectionId.LongServerValue` is null on them (verified, driver 3.12). Exporters skip
  handshake commands; the proxy's `conn` record covers them.

* No `$spyglass` seen for that address (for example, explicit config without a proxy, or a
  direct connection that bypasses the proxy): send `proxyId: null`. The collector stores the
  record as an orphan (§6), and it's only used for exporter-side statistics such as pool
  metrics.
* `serverConnectionId` is null (old server or failed handshake): send `null`, with the same
  orphan handling.

## 4. Records

Encoding:

* NDJSON: one JSON object per line, UTF-8, no BOM.
* Every record has `type` and `ts`.
* All timestamps are **integer microseconds since the Unix epoch, UTC** (`ts`, `*_us`).
  Microseconds fit in an IEEE-754 double exactly until year 2255; nanoseconds would not
  survive JavaScript JSON parsing.
* All durations are integer microseconds (`*_us`).
* 64-bit identifiers that may exceed 2⁵³−1 (`cursorId`) are JSON **strings** in decimal.
  `serverConnectionId` and `requestId` are JSON numbers (both are far below 2⁵³).
* **Absent means default.** In wire records (§4.2), a field that would be `null`, `false`, `0`
  or its stated default is omitted, and consumers MUST read an absent field as that value.
  The defaults: `opCode` = `"OP_MSG"`, booleans = `false`, counts = `0`, `reply.parsed` =
  `true`. Values the collector can derive are not sent at all; their derivations are listed
  under `op`.

### 4.1 Common envelope fields

| Field | Type | Required | Meaning |
|---|---|---|---|
| `type` | string | yes | record type |
| `ts` | int | yes | event time (µs since epoch) |
| `src` | string | yes | producer instance id (UUID per process start). For wire records it **is** the `proxyId`, which those records therefore don't repeat. |

### 4.2 Wire records (producer: proxy)

**`proxy`**: once per proxy start, then every 60 s.

```json
{"type":"proxy","ts":…,"src":"<proxyId>","version":"1.0.0","schema":1,
 "topology":"replicaSet","setName":"rs0",
 "members":[{"member":"localhost:27017","upstream":"rs0-a.example.net:27017"}, …],
 "config":{"compression":"strip","values":"truncated","commandBytes":16384}}
```

**`conn`**: once at open, once at close.

```json
{"type":"conn","ts":…,"src":"<proxyId>","event":"open|close",
 "member":"localhost:27017","connSeq":1234,
 "serverConnectionId":5512,                     // absent until the first hello reply
 "client":"10.1.2.3:51122","tls":true,          // absent = false
 "app":"orders-api","driver":"nodejs|spyglass-node","driverVersion":"7.7.0|0.1.0",   // absent if the client sent none
 "exporter":"spyglass-node",                     // absent if no marker
 "os":"linux","platform":"Node.js v22.4.0, LE",
 "closeReason":"client_closed|upstream_closed|invalid_frame|upstream_connect_failed|proxy_shutdown",   // close only
 "bytesIn":…, "bytesOut":…}                      // close only
```

**`op`**: one per operation (request plus its reply, or an unacknowledged request).

```json
{"type":"op","ts":<request tFirst as wall-clock µs>,"src":"<proxyId>",
 "member":"localhost:27017","serverConnectionId":5512,"requestId":88231,"connSeq":1234,
 "opCode":"OP_QUERY",                              // absent = "OP_MSG"
 "command":"find","db":"orders","coll":"orders",
 "stream":true,                                   // absent = false; a moreToCome stream started here
 "status":"ok|error|connection_closed|unacknowledged",
 "reqBytes":412,"respBytes":19384,                // respBytes absent = 0 (no reply)
 "t":{"reqLast_us":3,"respFirst_us":812,"respLast_us":845},  // relative to ts (= reqFirst); resp* absent without a reply
 "reply":{"ok":1,"code":11000,"codeName":"DuplicateKey","errmsg":"…",   // absent without a reply; each field absent if not in the reply
          "cursorId":"0","nReturned":17,"n":5,"nModified":5,
          "writeErrors":2,"writeConcernError":true,"upserted":1,           // absent = 0 / false
          "cursorsKilled":1,"cursorsNotFound":0,                           // killCursors replies: counts; both absent when both are 0
          "parsed":false},                                                 // absent = true: the scanner read the whole reply
 "lsid":"3fa1c09e5b7d2e11","txnNumber":7,         // absent outside a session / transaction
 "comment":"…",                                   // string comments only, truncated to 256; absent if none
 "cmd":"<base64 of redacted BSON command body>",  // absent if values=shape and not needed; see 01 §9
 "cmdTruncated":true,                             // absent = false
 "seqDocs":{"documents":120},                     // kind-1 sequence identifiers → counts; absent if none
 "compressor":"zlib"}                             // OP_COMPRESSED only
```

Derived by the consumer, not sent:

| Value | Derivation |
|---|---|
| `proxyId` | `src` |
| `ack` | `status != "unacknowledged"` |
| `serverLatency_us` | `t.respFirst_us − t.reqLast_us`, when both are present |
| `replyCount` | 0 if `status` is `unacknowledged` or `connection_closed`; else 1 + the `stream` records for the same key |
| `t.reqFirst_us` | 0 (it is `ts`) |

**`stream`**: an additional reply on a `moreToCome` stream (exhaust getMore, streaming hello).

```json
{"type":"stream","ts":…,"src":"<proxyId>","member":"…","serverConnectionId":5512,
 "requestId":88231,"seq":2,"respBytes":…,"reply":{"cursorId":"…","nReturned":101,"ok":1}}
```

**`stats`**: every 10 s from every producer, including exporters.

```json
{"type":"stats","ts":…,"src":"…","dropped":{"no_slot":0,"queue_full":0,"pending_evicted":0,"export":0},
 "sent":{"records":12001,"batches":7}}
```

`dropped` maps a reason to a count; consumers show whatever reasons arrive. The proxy's: `no_slot`
and `queue_full` (01 §4), `pending_evicted` (a request evicted from the per-connection pending table when it
exceeds `maxPendingPerConnection`, 01 §7), `export` (01 §11).

### 4.3 Context records (producer: exporter)

**`hello`**: once per exporter start (and after any endpoint change).

```json
{"type":"hello","ts":…,"src":"<exporter uuid>","exporter":"spyglass-dotnet","version":"0.1.0","schema":1,
 "runtime":"dotnet 10.0.1","driver":"mongo-csharp-driver 3.12.0",
 "host":"orders-api-7d9f-x2","pid":4121,"app":"orders-api","appVersion":"2.14.0",
 "sampling":{"callsites":"first+1/100"}}
```

**`cmd`**: one per command *attempt*, emitted when the attempt finishes. The key fields
mirror `op`.

```json
{"type":"cmd","ts":<CommandStarted wall-clock µs>,"src":"…",
 "proxyId":"…","member":"localhost:27017","serverConnectionId":5512,"requestId":88231,
 "operationId":"71",                       // driver operation id, groups retries and getMores; string
 "command":"find","db":"orders",
 "status":"ok|error",
 "client_us":1210,                         // driver-reported duration (started → succeeded/failed)
 "poolWait_us":3400,                       // time to check out the connection used, null if unknown (04 §5, 05 §5)
 "attempt":1,                              // >1 for driver retries, when determinable
 "uow":{"id":"4bf92f3577b34da6a3ce929d0e0e4736","name":"GET /orders/{id}","kind":"http"},  // null if none
 "trace":{"traceparent":"00-4bf9…-00f0…-01"},   // null if none
 "callsite":"9c1e33a70b2f44d1",            // FNV-1a 64 hex of the normalized frame list; null if not captured
 "cursorId":null,                          // decimal string; set when this command's reply opened a cursor (non-zero cursor.id) inside a UoW
 "errorCode":null,"errorLabels":null}      // on error: server code (or a client-side code name)
```

`proxyId`, `member` and `serverConnectionId` are always present, `null` when the exporter could not
determine them (§3.3); a record that leaves them out is malformed. `requestId`, `command`, `db` and
`status` are required. `errorCode` is a number (server code) or a string (client-side code name).

Note that `cmd` carries **no** collection, filter, values or reply content (I6):

* `db` and `command` are included because they're needed to validate joins (§6.3) and carry
  no user data.
* `cursorId` is an identifier, not content. It lets the collector attribute an open cursor
  to the UoW that leaked it (03 §5.2 `cursor-not-exhausted`).

**`callsite`**: once per distinct `callsite` hash per exporter process.

```json
{"type":"callsite","ts":…,"src":"…","callsite":"9c1e33a70b2f44d1","lang":"dotnet",
 "frames":[{"fn":"OrdersRepository.GetByIdAsync","file":"src/Orders/OrdersRepository.cs","line":42},
           {"fn":"OrdersController.Get","file":"src/Orders/OrdersController.cs","line":18}]}
```

* `frames` is ordered innermost first and limited to 16 user frames.
* File paths MUST be made relative to the repository/content root when it can be
  determined. Otherwise drop everything up to and including the last `/src/` or `/app/`
  segment, and never emit absolute home-directory paths.

**`uow`**: unit-of-work completion. Optional; lets the collector close a UoW without waiting
for its ops to age out.

```json
{"type":"uow","ts":<start µs>,"src":"…","id":"4bf92f…","name":"GET /orders/{id}","kind":"http",
 "duration_us":48210,"status":"200","cmdCount":53}
```

**`pool`**: every 10 s, per server address.

```json
{"type":"pool","ts":…,"src":"…","member":"localhost:27017","proxyId":"…",
 "maxSize":100,"total":100,"inUse":100,"available":0,"waitQueue":37,
 "checkouts":4310,"checkoutFailed":{"timeout":12,"poolClosed":0,"connectionError":0},
 "poolWait_us":{"p50":2100,"p95":48000,"max":30001000}}
```

## 5. Transport

* `POST {ingest}` — the URL from discovery or config, always ending in `/ingest/v0`.
* Headers:
  * `Content-Type: application/x-ndjson`
  * `Authorization: Bearer <token>` when a token is known
  * `X-Spyglass-Producer: proxy|spyglass-dotnet|spyglass-node`
  * `X-Spyglass-Src: <src uuid>`
  * optional `Content-Encoding: gzip`
* Batch limits: ≤ 2000 records and ≤ 1 MiB uncompressed. Producers flush at those limits or
  every 500 ms (proxy) / 1000 ms (exporters), whichever comes first.

Responses:

| Status | Meaning | Producer action |
|---|---|---|
| 204 | accepted (possibly partially; see `X-Spyglass-Rejected: <n>`) | none |
| 400 | malformed batch | drop the batch, count it, do not retry |
| 401 / 403 | bad or missing token | drop; exporters go dormant for 5 min, then retry discovery |
| 413 | batch too large | split once in half, then drop |
| 429 | overloaded | drop the batch; pause sending for `Retry-After` seconds (cap 10) |
| 5xx / network error | collector trouble | retry once after 1 s, then drop |

* Producers MUST bound their buffer (proxy: config; exporters: 4 MiB default) and count
  every dropped record in the next `stats`.
* Producers MUST NOT block the application or the forwarding path on transport.
* Delivery is **at most once**. Collectors MUST tolerate duplicates anyway (§6.4).

## 6. Join semantics (normative for the collector)

### 6.1 Matching

* A `cmd` matches an `op` iff their `joinKey`s are equal.
* At most one `cmd` joins one `op`. `stream` records attach to their `op` by
  `(proxyId, member, serverConnectionId, requestId)`.

### 6.2 Windows

* The collector holds an unmatched `op` for `joinWait`:
  * 2 s if the connection has no exporter marker;
  * 10 s if it has one;
  * not at all while no exporter has sent anything for 10 minutes (nothing could join in the wait),
    and never for hello-family commands (exporters skip handshakes and monitoring, §3.3).
* It holds an unmatched `cmd` for 30 s.
* Records that match after their counterpart was stored are **merged into the stored op**,
  provided they arrive within 10 minutes of the op's arrival. After that they're discarded and
  counted. The index of stored ops that a late `cmd` can still find is bounded by memory like
  dedup (§6.4): 2^17 keys per partition by default, about 10 minutes below ~200 ops/s per partition.
* Waiting is per arrival, not per event time: the windows run on the collector's clock, from
  when each record arrived.
* The waiting sets are bounded too (200,000 ops and 200,000 cmds by default, 03 §11). Past the
  bound the op closest to its deadline is stored early, wire-only, and counted; a `cmd` that
  arrives for it later still merges.

### 6.3 Validation

A match is accepted only if `command` and `db` agree. If they disagree, both records are
kept unjoined and `spyglass_collector_join_conflicts_total` is incremented. This is the
tripwire for the negligible collision case in §3.1.

### 6.4 Duplicates

Producers may resend after a timeout. The collector deduplicates by
`(src, type, joinKey, ts)` within the 10-minute window, bounded by memory: 2^18 key hashes per
partition (~8 MiB). That covers the whole window below ~440 records/s per partition, and about
10 s at the 03 §11 rate. Producers resend within seconds (§5: one retry after 1 s).

### 6.5 Orphans

* `cmd` records with `proxyId`, `member` or `serverConnectionId` null, or with no matching `op` after
  30 s, are counted per `src` and contribute only to exporter-side statistics.
* `op` records with no `cmd` are normal: they're simply wire-only.

## 7. Conformance

Test vectors live in `docs/spec/vectors/ingest-v0/`. They are specified in 06 §3:

* producers' encoders MUST round-trip them, and MUST NOT emit a field or JSON type their record
  type's full example does not have (the proxy is checked this way on a real workload);
* collectors MUST produce the documented joined output for each scenario file.
