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
its expiry. Expired `op`s are finalized as wire-only. Expired `cmd`s become orphans.

### 3.2 Waiting policy

`joinWait` is 2 s by default, and 10 s when the `conn` record for that
`(proxyId, member, serverConnectionId)` names an exporter. Finalized ops are visible in the
live feed **immediately**: the UI shows the op at once and patches it in place when context
arrives (SSE `op.updated`).

### 3.3 Joined operation (the stored entity)

```
Operation {
  id                     // collector-assigned ULID
  joinKey, connSeq
  ts, command, db, coll, opCode, status, ack, stream, replyCount
  reqBytes, respBytes, serverLatency_us, timings{reqFirst,reqLast,respFirst,respLast}
  reply facts            // from op
  cmd (redacted BSON), cmdTruncated, seqDocs
  shapeHash, valueHash   // §4
  app, driver, member, lsid, txnNumber, comment
  // from cmd (nullable)
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
* `shapeHash`: the first 16 hex chars of SHA-256 over the canonical relaxed-Extended-JSON
  serialization with keys sorted at every level;
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
| `find` | `filter: normFilter`, `sort` (keys and directions kept), `projection` (keys and inclusion kept), `limit`/`skip`/`batchSize` presence as `"?"`, `hint` (kept), `collation.locale` (kept) |
| `aggregate` | `pipeline`: per stage, `{<stageName>: normStage}`. `$match` → normFilter. `$sort`/`$project`/`$group._id` structure kept with leaves tagged. Accumulator operators kept. `$lookup` keeps `from`/`localField`/`foreignField`/`as` and normalizes its sub-pipeline. `$limit`/`$skip` → `"?"`. Unknown stages: leaves tagged. |
| `update` | `updates[]` → per statement `{q: normFilter, u: <operator names → field names → tag>, upsert, multi}`. Pipeline-style `u` is normalized like an aggregate pipeline. Statements deduplicated by canonical form. |
| `delete` | `deletes[]` → per statement `{q: normFilter, limit}` |
| `findAndModify` | `query`, `sort`, `update` (as above), `remove`/`new`/`upsert` flags |
| `count` / `distinct` | `query: normFilter`, plus `key` for distinct |
| `insert` | nothing beyond `coll`. The document count is a metric, not shape. |
| `getMore` | the shape of the op that opened the cursor (§5.4), so getMores group with their originating query |
| others | command name only |

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
| `slow-shape` | no | `serverLatency_us > threshold` | shapeHash | threshold 100 ms; also per-shape p95 regression ×3 vs the 1-hour baseline (needs ≥ 50 samples) |
| `n-plus-one` | yes (precise) | within one UoW: ≥ N ops of the same shapeHash with ≥ 2 distinct valueHashes | (uowName, shapeHash, callsite) | N = 10 |
| `n-plus-one-suspected` | no | same connection: ≥ N same-shape ops, each starting ≤ G after the previous reply's `respLast` (tight-loop signature) | (app, shapeHash) | N = 10, G = 5 ms. Labelled "suspected (no unit-of-work context)" |
| `repeated-query` | yes (precise) / no (suspected) | ≥ M ops with the same valueHash within one UoW (or within 1 s on one connection) | (uowName or app, valueHash) | M = 3 |
| `unbounded-scan` | no | `find`/`count` with an empty filter and no limit, or an aggregate without a leading `$match`/`$limit`, on a collection that has returned > 1000 docs in any op | (app, shapeHash) | — |
| `large-result` | no | `respBytes > 1 MiB` or `nReturned > 1000` (first batch) | shapeHash | — |
| `unbounded-in` | no | `inCardinality > 1000` | shapeHash | — |
| `cursor-abandoned` | no | §5.4 | (app, shapeHash) | — |
| `cursor-not-exhausted` | yes | the UoW ended while a cursor it opened was still open | (uowName, callsite) | grace 1 s |
| `pool-starvation` | yes | p95 `poolWait_us` > 10 ms over 1 min, or any `checkoutFailed.timeout > 0` | (app, member) | — |
| `client-overhead` | yes | `clientOverhead_us > max(5 ms, 50% of client_us)` for ≥ 20% of a shape's ops over 1 min | (app, shapeHash) | — |
| `error-burst` | no | > 5% errors for one `(app, command, coll)` over 1 min, with ≥ 20 ops | (app, command, coll, codeName) | — |
| `unacknowledged-writes` | no | any `ack=false` write (info) | (app, coll) | — |
| `exporter-skew` | yes | per `src`, \|clock skew\| > 1 s, or join conflicts > 0 | src | — |

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
| Callsites | until unused for 30 d | — |

Writes are batched (≥ 500 rows or 250 ms per transaction). Under write backpressure, raw
operations are sampled (keep every slow, errored or alerted op, plus 1/N of the rest)
before aggregates are ever dropped.

## 8. API (REST + SSE, JSON)

Base path `/api/v1`. All endpoints require auth (§9) except `/healthz`.

| Method & path | Purpose |
|---|---|
| `GET /ops?from&to&app&coll&command&shape&uow&status&minLatencyUs&limit&cursor` | Operation search, newest first, cursor-paginated |
| `GET /ops/{id}` | Operation detail: command (EJSON), facts, timing waterfall, joined context, cursor, linked alerts |
| `GET /shapes?from&to&app&coll&sort=totalTime\|p95\|count\|errors` | Shape leaderboard with aggregates |
| `GET /shapes/{hash}` | Shape detail: normalized shape, latency series, examples (slowest 10 plus latest 10), callsites |
| `GET /uows?from&to&name&minOps&minTimeUs` | Units of work |
| `GET /uows/{id}` | UoW detail: ordered op list (HR-profiler "statements in this request" view) |
| `GET /alerts?state=open\|resolved&rule` · `POST /alerts/{id}/mute` | Alerts |
| `GET /cursors?state=open\|closed&reason` | Cursors |
| `GET /connections` · `GET /apps` | Live connections and apps (from `conn` records) |
| `GET /producers` | Proxies and exporters: version, last seen, drops, skew, join rate |
| `GET /live?filter=…` | SSE stream: `op.created`, `op.updated`, `alert.opened`, `alert.updated` |
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
| Join CPU per op | ≤ 5 µs |
| Memory | bounded by configured queues plus UoW/join state limits; worst case logged at startup |
