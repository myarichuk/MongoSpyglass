# 03 — spyglass-collector v1

One container: ingest endpoint, join engine, shape normalizer, analyzers, storage, REST/SSE
API, and static UI. It holds no MongoDB credentials and never talks to the user's database.

## 1. Pipeline

```
POST /ingest/v0 ─▶ validate ─▶ dedup ─▶ join (02 §6) ─▶ enrich (shape, valueHash, UoW) ─▶ analyzers ─▶ store
                                                                                     └─▶ live fan-out (SSE)
```

* Ingest MUST respond within 50 ms. It enqueues into a bounded in-memory queue (default
  256 MiB) and returns 429 when the queue is above 90%.
* Everything after ingest runs on a fixed pool of workers, partitioned by
  `(proxyId, member, serverConnectionId)`. That partitioning keeps per-connection order and
  makes joins lock-free.

## 2. Validation

* Unknown `type`s are ignored. Unknown fields are kept in the raw record and otherwise
  ignored.
* Records missing required fields (02 §4) are rejected individually, and the count is
  returned in `X-Spyglass-Rejected`.
* `ts` more than 5 minutes in the future (relative to collector time) is clamped and counted
  as clock skew per `src`. It's shown on the Proxies/Exporters page.

## 3. Join engine

Implements 02 §6 exactly.

### 3.1 State

Per partition, two maps keyed by `joinKey`: unmatched `op`s and unmatched `cmd`s, each with
its expiry. Expired `op`s are finalized as wire-only. Expired `cmd`s become orphans. A third,
bounded map holds the keys of finalized ops (02 §6.2), so a late `cmd` finds the stored op it
merges into. Each map is a dictionary of structs plus a FIFO of keys per wait length, so expiry
is O(expired) and nothing is allocated per op beyond the entries. Measured: 0.78 µs and 95 B per
op + cmd pair (§11).

### 3.2 Waiting policy

`joinWait` is 2 s by default, and 10 s when the `conn` record for that
`(proxyId, member, serverConnectionId)` names an exporter. Ops don't wait while no exporter has
reported for 10 minutes, and hello-family ops never wait (02 §6.2).

An op goes through the analysis stage **on arrival**, whether it waits or not: wire rules,
aggregates, cursors, and the live feed see it at once (§11's live-feed latency holds with
exporters). What waits is storage: the op is written when it is final (joined, or its wait ran
out), so a joined op is written once. While it waits, `/ops/{id}` serves it from memory. When
context arrives the live row is patched in place (SSE `op.updated`). Context for an op already
stored (a late `cmd`) is an update that goes through the storage writer's FIFO behind the op's own
append; stream replies that arrive after storage do the same.

### 3.3 Joined operation (the stored entity)

```
Operation {
  id                     // collector-assigned ULID
  joinKey, connSeq
  ts, command, db, coll, opCode, status, ack, stream, replyCount      // ack, replyCount derived at ingest (02 §4.2)
  reqBytes, respBytes, serverLatency_us, timings{reqFirst,reqLast,respFirst,respLast}   // serverLatency_us derived
  reply facts            // from op
  cmd (redacted BSON), cmdTruncated, seqDocs
  shapeHash, valueHash   // §4
  app, driver, member, lsid, txnNumber, comment
  // from cmd (nullable): the cmd record itself, stored as its NDJSON line next to the op's
  operationId, client_us, poolWait_us, attempt, uowId, uowName, traceparent, callsite
  // derived
  clientOverhead_us      // client_us − (respLast − reqFirst), clamped ≥ 0
}
```

## 4. Shape normalization

The shape is computed in the collector only, so there's a single implementation, from the
redacted command BSON.

**Input.** The command document and its kind-1 sequences, with redacted values left as is.

**Output.**

* `shape`: a canonical document;
* `shapeHash`: the first 16 hex chars of SHA-256 over the canonical serialization: compact
  relaxed Extended JSON v2 (no whitespace), keys sorted by ordinal at every level, arrays in
  order. The collector writes it itself, straight from the command's BSON bytes: no document is
  decoded and no shape tree built (3.3 µs for shape and value, against 10.6 µs decoding a tree,
  and the driver's JSON writer cost more than that). A differential test pins the property that matters:
  two commands get the same canonical bytes exactly when the driver's sorted relaxed EJSON is
  the same;
* `valueHash`: the same, over the value-preserving normalization. It's not computed when
  the proxy runs `values: shape`, or when `cmdTruncated` is true.

### 4.1 Leaf normalization (shape)

| Value | Replaced by |
|---|---|
| int32 / int64 / double / decimal128 | `"?number"` |
| string | `"?string"` |
| ObjectId | `"?objectId"` |
| date | `"?date"` |
| bool | `"?bool"` |
| null | `"?null"` |
| BinData | `"?binData"` (subtype 4 → `"?uuid"`) |
| regex | `"?regex"` |
| `"$redacted"` | `"?redacted"` |

### 4.2 Filter normalization

`normFilter(doc)`:

* Each top-level key is either a field path or a logical operator.
* `$and` / `$or` / `$nor`: map `normFilter` over the elements, sort the results by their
  canonical serialization, and deduplicate.
* Field path with a non-operator value (equality): the leaf tag. A document value with no
  `$` keys is treated as exact-match `"?document"`.
* Field path with an operator document: a document of operator → normalized operand:
  * `$in`, `$nin`, `$all` → `["?"]` (cardinality is recorded separately as
    `inCardinality = max length`);
  * `$exists`, `$type` → the literal value (semantically significant);
  * `$regex` and `$options` → `"?regex"`;
  * `$elemMatch` → `normFilter`;
  * `$not` → normalize its operand;
  * `$size` → `"?number"`;
  * anything else → leaf tag of the operand.
* Keys are sorted. (MongoDB 8.0's server-side query shapes keep order; v1 deliberately
  groups order-insensitively.)

### 4.3 Per-command shape

The shape always includes `cmd` (command name), `db`, `coll`.

| Command | Shape adds |
|---|---|
| `find` | `filter: normFilter`, `sort` (keys, order and directions kept, as `[[key, dir], …]`), `projection` (keys and inclusion kept), `limit`/`skip`/`batchSize` presence as `"?"`, `hint` (kept; a key pattern as `[[key, dir], …]`), `collation.locale` (kept) |
| `aggregate` | `pipeline`: per stage, `{<stageName>: normStage}`. `$match` → normFilter. `$sort` as `[[key, dir], …]`. `$project`: inclusion flags kept, expressions with leaves tagged. `$group._id` structure kept with leaves tagged. Accumulator operators kept. `$lookup` keeps `from`/`localField`/`foreignField`/`as` and normalizes its sub-pipeline. `$limit`/`$skip` → `"?"`. Unknown stages: leaves tagged. |
| `update` | `updates[]` → per statement `{q: normFilter, u: <operator names → field names → tag>, upsert, multi}`. Pipeline-style `u` is normalized like an aggregate pipeline. Statements deduplicated by canonical form. |
| `delete` | `deletes[]` → per statement `{q: normFilter, limit}` |
| `findAndModify` | `query`, `sort`, `update` (as above), `remove`/`new`/`upsert` flags |
| `count` / `distinct` | `query: normFilter`, plus `key` for distinct |
| `insert` | nothing beyond `coll`. The document count is a metric, not shape. |
| `getMore` | the shape of the op that opened the cursor (§5.4), so getMores group with their originating query |
| others | command name only |
| auth commands (01 §9.1, sent as `{$redacted: name}`) | command name only, no valueHash |

Key order is significant in a sort and an index key pattern, and the canonical form sorts document
keys, so those are written as arrays of pairs. (M2: the shape vectors caught `sort {a:1, b:1}` and
`sort {b:1, a:1}` grouping together, and `$project {x:1}` with `$project {x:0}`.)

With the proxy on `values: shape` (01 §9.3) every leaf arrives as its tag, including the literals a
shape keeps: sort directions, `$exists`/`$type` operands, projection flags, a delete's `limit`.
Shapes then group more coarsely (`sort {ts: 1}` with `sort {ts: -1}`). That is the price of that
mode; the vectors pin it (`shape/proxy-encodings.json`).

### 4.4 valueHash

Same structure as the shape, with original (redacted, possibly truncated) leaf values instead
of tags, and arrays kept in order.

## 5. Analyzers

All analyzers are plain incremental code over joined operations. There is no rule engine.

Each analyzer:

* keeps O(1) work per operation;
* bounds its state per key and in total;
* emits **alerts** keyed by `(rule, scope)`.

### 5.1 Alert model

```
Alert { id, rule, scope{shapeHash?, coll?, app?, member?, uowName?, callsite?}, severity,
        firstSeen, lastSeen, occurrences, sampleOpIds[≤10], summary, details }
```

* One alert per `(rule, scope)`. New occurrences bump `occurrences` and `lastSeen`, and keep
  up to 10 sample op ids.
* An alert is **resolved** automatically after `quietPeriod` (rule-specific) with no
  occurrences. A later occurrence reopens it.
* Users can mute a `(rule, scope)`.

### 5.2 Rules

| Rule | Needs exporter | Trigger | Scope | Default |
|---|---|---|---|---|
| `slow-shape` | no | `serverLatency_us > threshold`, except ops that wait by design (hello family, tailable/awaitData cursors, change streams, and their getMores) | shapeHash | threshold 100 ms; also per-shape p95 regression ×3 vs the 1-hour baseline (needs ≥ 50 samples) |
| `n-plus-one` | yes (precise) | within one UoW: ≥ N ops of the same shapeHash with ≥ 2 distinct valueHashes | (uowName, shapeHash, callsite) | N = 10 |
| `n-plus-one-suspected` | no | same connection: ≥ N same-shape ops, each starting ≤ G after the previous reply's `respLast` (tight-loop signature) | (app, shapeHash) | N = 10, G = 5 ms. Labelled "suspected (no unit-of-work context)" |
| `repeated-query` | yes (precise) / no (suspected) | ≥ M ops with the same valueHash within one UoW (or within 1 s on one connection) | (uowName or app, shapeHash); the repeated values are the samples | M = 3 |
| `unbounded-scan` | no | `find`/`count` with an empty filter and no limit, or an aggregate without a leading `$match`/`$limit`, on a collection that has returned > 1000 docs in any op or cursor | (app, shapeHash) | — |
| `large-result` | no | `respBytes > 1 MiB` or `nReturned > 1000` (first batch) | shapeHash | — |
| `unbounded-in` | no | `inCardinality > 1000` | shapeHash | — |
| `cursor-abandoned` | no | §5.4 | (app, shapeHash) | — |
| `cursor-not-exhausted` | yes | the UoW ended while a cursor it opened was still open | (uowName, callsite) | grace 1 s |
| `pool-starvation` | yes | p95 `poolWait_us` > 10 ms over 1 min, or any `checkoutFailed.timeout > 0` | (app, member) | — |
| `client-overhead` | yes | `clientOverhead_us > max(5 ms, 50% of client_us)` for ≥ 20% of a shape's ops over 1 min | (app, shapeHash) | — |
| `error-burst` | no | > 5% failures for one `(app, command, coll)` over 1 min, with ≥ 20 ops. A failure is `ok:0`, or `ok:1` with write errors or a write-concern error | (app, command, coll, codeName) | — |
| `unacknowledged-writes` | no | any `ack=false` write (info) | (app, coll) | — |
| `exporter-skew` | yes | per `src`, \|clock skew\| > 1 s, or join conflicts > 0 | src | skew = median of `cmd.ts − op.ts` over the last 64 joins (on one clock: minus the client→proxy latency, well under 1 ms on a LAN) |

Notes from running these against real drivers (M1):

* `repeated-query` was scoped by `valueHash`. That gave one alert per repeated value: a loop
  over 20 ids repeated a few times opened 20 alerts for one defect.
* "Returned > 1000 docs in any op" never held. Drivers cap a getMore batch (Node: 1000), so a
  3000-document scan never returns more than 1000 in one reply. Cursor totals are counted too.
* A duplicate key is `ok:1` with `writeErrors`, not `ok:0`. Counting only `ok:0` hid the
  commonest failure an application sees. The per-shape aggregate `errors` uses the same
  definition.

### 5.3 Unit of work

* A UoW is identified by `uowId` from `cmd` records.
* Its op list is accumulated in memory until a `uow` record arrives, or until 30 s pass
  without a new op.
* At UoW close, compute: op count, total `serverLatency_us`, total `poolWait_us`,
  per-shape counts. Then run the UoW-scoped rules.
* Memory bound: 10,000 open UoWs (LRU, oldest closed early) × 1,000 ops each. Ops beyond
  that are counted but not listed.

### 5.4 Cursors

Keyed by `(member, cursorId)`. Never by connection: drivers send `getMore` over any pooled
connection to the same server.

| Event | Effect |
|---|---|
| `find`/`aggregate`/`listCollections`/`listIndexes` reply with `cursorId ≠ 0` | open. Record the originating op, shapeHash, app, uowId, the open time, and the first batch `nReturned`. |
| `getMore` (by the request's cursor id) | touch: `lastActivity`, add `nReturned` |
| reply with `cursorId = 0` on a getMore of that cursor | close (`exhausted`) |
| `killCursors` reply listing the id in `cursorsKilled` | close (`killed`) |
| `getMore` error with codeName `CursorNotFound` | close (`server-expired`), and raise `cursor-abandoned` for its shape |
| no activity for `cursorTimeout` (default 10 min, = mongod `cursorTimeoutMillis`) | close (`timed-out`), and raise `cursor-abandoned` |

`killCursors` and connection close do **not** imply abandonment. A connection closing has no
effect on cursors.

Cursor metrics per shape:

* time to exhaustion;
* total documents;
* getMore count;
* closure reason distribution.

## 6. Aggregates

Per `(shapeHash, minute)` bucket:

* `count`, `errors`;
* `serverLatency` as a DDSketch (relative accuracy 1%) or an HDR histogram
  (1 µs – 60 s, 2 significant digits);
* `sum(respBytes)`, `sum(reqBytes)`, `sum(nReturned)`, `max(nReturned)`;
* `sum(poolWait_us)` and `sum(client_us)` when present.

Buckets roll up to hourly after 48 h.

## 7. Storage

* **Interface:** `IOperationStore`, `IAggregateStore`, `IAlertStore`, `IUowStore`,
  `ICallsiteStore`.
* **v1 default implementation:** embedded **SQLite** (WAL mode, single file at `/data`).
  It's the smallest-footprint option that is queryable without a separate service.
  Alternatives (RavenDB, ClickHouse) are adapters behind the same interface, and are not
  part of v1.

| Data | Retention default | Bound |
|---|---|---|
| Operations (joined, including redacted command) | 24 h | 5 GiB, oldest deleted first |
| Aggregates, minute | 48 h | — |
| Aggregates, hour | 30 d | — |
| Alerts | 30 d after resolve | — |
| UoW summaries | 7 d | — |
| Callsites | until unused for 30 d (used = a joined op referenced it; refreshed hourly) | — |

Writes are batched (≥ 500 rows or 250 ms per transaction). Under write backpressure, raw
operations are sampled (keep every slow, errored or alerted op, plus 1/N of the rest)
before aggregates are ever dropped.

## 8. API (REST + SSE, JSON)

Base path `/api/v1`. All endpoints require auth (§9) except `/healthz` and `/readyz`, which report
states and counts only.

| Path | 200 when | 503 when |
|---|---|---|
| `GET /healthz` (liveness; the container HEALTHCHECK) | every stage is turning and storage is writing | a partition or the analysis stage hasn't turned for 30 s, the storage writer for 120 s (its longest legitimate step is one retention chunk), or storage writes have failed for 60 s with no success |
| `GET /readyz` | live, and not shedding ingest | not live, or the ingest queue is above 90 % (ingest answers 429) |

Both return `{status, live, ready, checks: {<name>: {ok, detail}}}`. Load is a readiness matter, not a
liveness one: restarting a collector that sheds load loses its in-memory state and fixes nothing.

| Method & path | Purpose |
|---|---|
| `GET /ops?from&to&app&coll&command&shape&uow&status&minLatencyUs&limit&cursor` | Operation search, newest first, cursor-paginated. Joined ops carry `joined`, `client_us`, `poolWait_us`, `uowId`, `uowName`. |
| `GET /ops/{id}` | Operation detail: command (EJSON), facts, timing waterfall, joined `context` (the cmd, `clientOverhead_us`, callsite frames), cursor, linked alerts. Also serves an op still waiting for its context. |
| `GET /shapes?from&to&app&coll&sort=totalTime\|p95\|count\|errors` | Shape leaderboard with aggregates |
| `GET /shapes/{hash}` | Shape detail: normalized shape, latency series, examples (slowest 10 plus latest 10), callsites |
| `GET /uows?from&to&name&minOps&minTimeUs` | Units of work |
| `GET /uows/{id}` | UoW detail: ordered op list (HR-profiler "statements in this request" view) |
| `GET /alerts?state=open\|resolved&rule` · `POST /alerts/{id}/mute` | Alerts |
| `GET /cursors?state=open\|closed&reason` | Cursors |
| `GET /connections` · `GET /apps` | Live connections and apps (from `conn` records) |
| `GET /producers` | Proxies and exporters: version, last seen, drops, skew; per exporter `join {cmds, joined, orphans, conflicts, rate, skew_us}` |
| `GET /live?app&coll&command&status&minLatencyUs&uow` | SSE stream: `op.created`, `op.updated`, `alert.opened`, `alert.updated` |
| `POST /export?from&to&…` | NDJSON download of ops and alerts for offline sharing |
| `GET /metrics` | collector self-telemetry (Prometheus) |

## 9. Security

* **Ingest:** a bearer token per deployment (`SPYGLASS_TOKEN`). The collector MUST reject
  ingest without it unless `SPYGLASS_INGEST_INSECURE=1`.
* **UI/API:** v1 supports one of:
  * a static admin password, as HTTP Basic or a login form with an HttpOnly session cookie;
  * a trusted reverse-proxy header (`SPYGLASS_AUTH_HEADER=X-Forwarded-User`) for
    oauth2-proxy and similar.

  There is no anonymous mode except `SPYGLASS_AUTH=none` *and* a loopback bind.
* **Binding:** API and UI bind to loopback by default.
* **Data:** stored data is already redacted by the proxy (I7). The collector MUST NOT accept
  `op` records whose `cmd` decodes to a redacted-command name with a non-redacted body
  (defence in depth: such records are rejected and counted).
* **Export:** the export file includes the redaction mode in its header.

## 10. UI

A static SPA served from `/`, talking only to `/api/v1`. It's built with Blazor WebAssembly
(standalone) to reuse the existing Tailwind theme and components. It holds no server-side
circuit state.

| Page | Content |
|---|---|
| **Live** | Streaming op table: time, app, command, ns, server latency, client latency (if joined), docs, bytes, status, UoW badge. Filters persist in the URL. Paused automatically when scrolled. |
| **Operation** | Command (shell-syntax EJSON, copy-to-clipboard as a mongosh command), reply facts, **timing waterfall** (pool wait → client→proxy → proxy→server+server → server→client), alerts, cursor lifecycle, callsite frames, "other ops in this UoW" |
| **Shapes** | Leaderboard by total time / p95 / count / errors. Sparkline per shape. Click through to shape detail with examples and callsites. |
| **Units of work** | List by duration / op count. Detail is the HR-profiler view: numbered statements, duplicated shapes highlighted, N+1 groups collapsed with counts. |
| **Alerts** | Grouped by rule, with occurrence counts and sample ops, mute/unmute |
| **Cursors** | Open cursors with age; closed cursors by reason |
| **Topology & producers** | Proxies, members and listeners, exporters, drops, join rate, clock skew, versions |

* No external network fetches: fonts and icons are bundled.
* Dark and light themes.
* Every table has keyboard navigation and copyable cells.

## 11. Collector performance targets

| Metric | Target |
|---|---|
| Sustained ingest | 50k records/s on 4 vCPU |
| Op visible in the live feed after the proxy flush | ≤ 1 s (p95) |
| Join CPU per op | ≤ 5 µs (measured: 0.78 µs per op + cmd pair, `JoinBenchTests`) |
| Memory | bounded by configured queues plus UoW/join state limits; worst case logged at startup |

## 12. Container

`src/Spyglass.Collector/Dockerfile` (build context: the repository root).

* **Base:** `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled`. No shell, no package manager, no
  shared framework, non-root (`$APP_UID`). The app is self-contained, trimmed and ReadyToRun.
* **Size:** ~57 MB of app on a ~13 MB base. Of the app, 11 MB is the UI with its precompressed `.br`/`.gz`
  siblings; ReadyToRun accounts for 13 MB.
* **Trimming:** the collector's own JSON is source-generated or written by hand. MongoDB.Bson isn't
  trim-annotated, so it is kept whole. The trimmed publish has no warnings, and the build was driven
  end to end: join, UI, and a restart that reloads shapes, alerts and callsites.
* **ReadyToRun:** the HEALTHCHECK probe is a fresh process every 30 s. ReadyToRun takes it from 0.2 s to
  0.05 s of CPU (from 0.4 s with `HttpClient`, before the probe moved to a raw socket). It also cuts
  startup from 1.2 s to 0.4 s. Idle RSS is ~54 MB.
* **Health:** `HEALTHCHECK CMD ["/app/spyglass-collector", "healthcheck"]` GETs `/healthz` on the
  `SPYGLASS_BIND` listener (a wildcard is probed on loopback), and exits 0 or 1.
* **UI serving:** a fingerprinted asset (`_framework/*.<10 chars>.*`) is `immutable` for a year; everything
  else is `no-cache` with an ETag. `.br` is served when the browser accepts it, otherwise `.gz`. Browsers
  offer brotli only over HTTPS, so plain-HTTP deployments get gzip. The 3.0 MB runtime travels as 0.98 MB
  (brotli) or 1.2 MB (gzip).
* **Configuration:** `SPYGLASS_*` environment variables. The image sets `SPYGLASS_BIND=0.0.0.0:8080` and
  `SPYGLASS_DATA=/data` (a volume). A non-loopback bind needs password or header auth (§9), so a
  `docker run` passes `SPYGLASS_TOKEN` and `SPYGLASS_ADMIN_PASSWORD` (or `SPYGLASS_AUTH_HEADER`).
* **Multi-arch:** the SDK stage runs on the build platform and publishes for `TARGETARCH`; crossgen2
  cross-compiles. The NuGet cache is a BuildKit cache mount, and restore sits on its own layer.

```sh
docker build -f src/Spyglass.Collector/Dockerfile -t spyglass-collector .
docker run -d --name spyglass -p 8080:8080 -v spyglass-data:/data \
  -e SPYGLASS_TOKEN=… -e SPYGLASS_ADMIN_PASSWORD=… spyglass-collector
```
