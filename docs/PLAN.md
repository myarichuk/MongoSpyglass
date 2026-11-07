# What comes next

Written 2026-11-07, after a review of the code at `fb0c600` with both suites green (proxy 91/3 skipped against
mongod 8.0.23, collector 169/2 skipped). The review's conclusion: the forwarding path, the observer and the
collector pipeline are sound and measured; what is missing is around them. This document orders the work, says
why each item is where it is, names the code it touches, and gives each a test that says when it is done.

Estimates are for one person who knows the code. Items within a phase are independent unless stated.

| Phase | Theme | Size |
|---|---|---|
| 0 | Front door (done in the commit that adds this file) | — |
| 1 | Safe to leave running: the collector under load and over weeks | ~1.5 weeks |
| 2 | "Which service?": a per-app dimension in the aggregates | ~1 week |
| 3 | The .NET exporter (M3): the product | ~3 weeks |
| 4 | Load-test workflow: runs, comparison, thresholds | ~2 weeks |
| 5 | Server-side truth: why a shape is slow | ~1.5 weeks |
| 6 | Operability: notifications, deployment, security posture | ongoing |
| 7 | The Node exporter (M4) | ~2 weeks |

---

## Phase 0 — front door (done)

The previous code (`MongoSpyglass.Service`, `MongoSpyglass.Proxy`, their tests, `Playground`, `Benchmarks`,
`tools/ReviewHarness`, the NRules plans) is deleted. The README describes the current architecture. The compose
file runs the current stack: mongod, proxy, collector, demo service. The proxy has a container image
(`src/Spyglass.Proxy/Dockerfile`) and a `healthcheck` subcommand for it, like the collector's.

---

## Phase 1 — safe to leave running

These are the defects that show up only after hours of traffic or under load. They are small individually
and they are first because everything after them assumes a collector that stays healthy for weeks.

### 1.1 Take the analysis thread off the disk

**Problem.** `AnalysisStage.Flush` (`src/Spyglass.Collector/Analysis/AnalysisStage.cs`) runs every 250 ms on
the one thread that owns cursors, aggregates and alerts, and calls `SqliteStore.Merge`, `UpsertShapes` and
`Upsert(alerts)` directly. `Merge` does a SELECT, decode, merge, encode and INSERT per dirty `(shape, minute)`;
`UpsertShapes` re-serializes every shape touched since the last flush (every op touches its shape's
examples). All of it runs under `SqliteStore._writeLock`, which the storage writer holds for 500-row
transactions and retention holds for 5,000-row deletes. A few thousand live shapes cost a third of the
analysis thread; a retention pass stalls it outright; a stalled analysis thread backs ingest up to 429.

**Change.** The analysis thread hands `TakeDirty()`'s deltas and shape snapshots, and the dirty alert
snapshots, to `StorageWriter` as items on its existing FIFO (new cases in `StorageWriter.Add`), and the
writer applies them inside its batch transaction. `IAggregateStore.Merge` and `IAlertStore.Upsert` stay as
they are; only the caller moves. The in-memory `AggregateBook` remains the read model for open minutes, so
`/shapes` sees nothing different.

Two details. `Housekeeping` comments that "the store must hold every delta before a minute closes": that
ordering is still kept because the writer's FIFO is in order and `CloseBefore` only drops buckets from memory
that the store already has a delta for; make `CloseBefore` wait for a writer barrier once a minute rather than
flushing synchronously. And `Merge` should batch: one `SELECT … WHERE (shape, minute) IN (…)` per transaction
instead of one per delta, since the writer now sees all deltas of a flush together.

**Done when.** `PipelineTests` keep passing against SQLite; a new test holds the write lock for 2 s from
another thread and asserts the analysis stage's `LastBeat` keeps advancing and ops keep appearing on the
live feed. `IngestBenchTests` shows the analysed rate unchanged or better.

### 1.2 Bound the connection registry

**Problem.** `ConnectionRegistry.Sweep` (`src/Spyglass.Collector/Pipeline/Registries.cs`) removes only
entries that have a `ClosedTs`. The proxy's `CollectorSender` drops whole batches on 429 and after a failed
retry, and `conn close` records go with them. A collector that ever sheds load keeps those connections forever;
`/connections` and `/apps` iterate the whole dictionary.

**Change.** Keep `LastSeenTick` on `ConnectionInfo`, touched by `Partition.Enrich` when an op is attributed
(one write per op on a per-connection object: no sharing between partitions since a connection lives in one).
`Sweep` also removes open entries idle longer than a bound (default 2 h, configurable), and the registry has a
hard cap (default 200k) past which the least recently seen entry goes. Count both evictions in `/metrics`.

The proxy side deserves a small change too: `conn` records are tiny and few, and losing one costs attribution
for a connection's whole life. Give them a reserved slice of `collector.bufferBytes` (a second
`RecordBuffer` per worker whose chunks are never dropped by `Paused`, only by the byte bound), so a 429 pause
drops ops but not connection lifecycle.

**Done when.** A test ingests 10k `conn open` records with no closes and asserts the registry is at the cap
after sweep; `SenderTests` has a case where a paused sender still delivers `conn` records.

### 1.3 Alerts: one event per change, not one per op

**Problem.** `AnalysisStage.Hit` publishes an `alert.updated` live event and dirties the alert on every
occurrence. A shape over the 100 ms threshold at 1,000 ops/s produces 1,000 alert events per second to every
SSE subscriber, because the live filter in `Server.Live` passes every event that has no op. `AlertBook.Publish`
then rebuilds the full snapshot every flush.

**Change.** `Hit` still counts occurrences in memory, but publishes `alert.opened` immediately and
`alert.updated` at most once per flush per alert (a dirty set, which already exists as `_dirtyAlerts`). The
live feed gets an `events=` filter so the Live page subscribes to `op.*` only and the Alerts page to `alert.*`
only. `AlertBook.Publish` snapshots only dirty alerts into a copy-on-write array.

**Done when.** A test drives 10k slow ops through one shape and asserts a subscriber received one
`alert.opened` and at most one `alert.updated` per 250 ms window.

### 1.4 The shape leaderboard must not decode the world

**Problem.** `Server.Shapes` loads every shape from SQLite and every `agg_minute` row in range, decodes each
into a `MinuteBucket` with a `SortedDictionary` sketch, groups and sorts in memory, per request. 24 hours
with 2,000 shapes is about 2.9 million rows decoded per page view; the UI offers 7 days.

**Change.** Two parts.

* Maintain the sums the leaderboard needs at write time: an `agg_shape_hour` table `(shape, hour, count,
  errors, sum_latency, sketch)` updated by the writer when it merges minute deltas (the hourly rollup already
  does this after 48 h; do it on the way in). The leaderboard for ranges ≥ 6 h reads hourly rows; 15 min and
  1 h read minute rows, which is at most 60 per shape.
* Sort in SQL where it does not need the sketch: `totalTime`, `count` and `errors` become
  `ORDER BY sum(...) LIMIT n` over the pre-summed rows; only the `p95` sort needs sketches, and only for the
  shapes in range.

The sparkline (`series`) is per shape; compute it only for the rows returned, not for every shape in range.

**Done when.** A test stores 2,000 shapes × 24 h of minute buckets and asserts `/shapes?from=…24h` answers in
under 200 ms on the test machine; `ApiTests` keep passing.

### 1.5 Small O(n)s that the rest of the code would not tolerate

* `latency` on `ops` has no index: `GET /ops?minLatencyUs=` alone is a table scan with LIMIT. Add
  `CREATE INDEX ops_latency ON ops(latency, id) WHERE latency IS NOT NULL` (partial, like `ops_status`).
* `Server.Op` finds an op's cursor by scanning up to 110k open and closed cursors. Publish a
  `Dictionary<(proxy, member, cursorId), CursorInfo>` snapshot from `Housekeeping` next to the arrays.
* `AlertBook.SetMuted` is linear in alerts; keep an id index.
* `ConnectionRegistry.All` is sorted in full for `/connections`; cap and paginate.

### 1.6 CI

There is none. The perf budgets in 06 §4 are only real if something runs them.

* GitHub Actions on every push and PR: `dotnet build -warnaserror`, collector tests, proxy tests Debug with
  `tools/mongod/fetch.sh` cached by version (the conda package is ~150 MB) and Node for
  `tests/node-conformance`. About 6 minutes.
* Nightly: both suites in Release (allocation and CPU gates), `tools/ProxyBench` three rounds with the proxy
  pinned to its own cores, JSON output compared with the previous nightly, more than 10 % regression on any
  budget line fails. Publish both container images on tags.

**Done when.** A PR that adds an allocation to `Pump.ProcessAsync` goes red.

---

## Phase 2 — "which service?"

**Problem.** Aggregates are keyed by `(shapeHash, minute)` (`AggregateBook.Add`, `agg_minute`). The
`app` filter on `/shapes` is `Shape.Apps.Contains(app)`: it selects shapes the app has ever run and shows
numbers that include every other app. Per-service load and latency cannot be answered from the collector at
all, only from the proxy's Prometheus metrics, which do carry `app`. In a multi-service staging environment
this is the first question anyone asks, and the second is "did service X's pattern change".

**Change.** Add `app` to the bucket key: `(shapeHash, app, minute)` in `AggregateBook`, `agg_minute`,
`agg_hour` and the Phase 1.4 hourly sums; `app` is `""` when unknown. `/shapes?app=` then sums only that app's
buckets. Add `GET /apps/{app}` with the app's own leaderboard and totals, and `GET /apps` with per-app ops per
second, p95 and error rate over the range, from the same rows. In the UI: the shapes page gets a "group by
app" toggle, and the Topology & producers page becomes "Services", one card per app with its drivers,
connections, exporter presence and the headline numbers.

Storage grows by the number of distinct `(shape, app)` pairs per minute, which in practice is close to the
number of shapes: a shape belongs to one service. Migrate by adding the column with `''` for existing rows.

Decide now, with this change, whether `proxyId` belongs in the key too. Two proxies for two clusters feeding
one collector blend their shapes today. The cheap answer is "one collector per upstream deployment", written
down; the right answer is the key. The migration is the same either way, so make the call once.

**Done when.** Two apps run the same shape at different latencies through `PipelineTests`, and
`/shapes?app=a` reports only a's p95.

---

## Phase 3 — the .NET exporter (M3)

This is the product. Everything the join engine, the vectors, the `uow` filter, the context panel and the
callsite store were built for is waiting on a package someone can add to a service in one line. Without it
MongoSpyglass is a wire profiler. With it, it is the thing the spec's first page promises: "which HTTP request
issued this, how long it waited for a connection, where in the code".

The spec is `docs/spec/04-exporter-dotnet.md` and it is detailed enough to build from. Order of work:

1. **Package skeleton and discovery** (`src/Spyglass.MongoDb.Exporter`): `AddSpyglass()` on
   `MongoClientSettings`, `ClusterConfigurator` composition, `LibraryInfo` marker, discovery from `$spyglass`
   in `ServerHeartbeatSucceededEvent`, dormant until discovered (I5: a service that never meets a proxy pays
   nothing). The `EndToEndTests` minimal exporter is the starting point; it already joins 100 %.
2. **`cmd` records** from `CommandStarted/Succeeded/Failed`: key from `RequestId` and
   `ConnectionId.LongServerValue`, handshake events skipped, timing by the exporter's own clock (05 §4's lesson:
   driver durations are coarse). Pool wait from `ConnectionPoolCheckedOutConnectionEvent`.
3. **Units of work**: `Activity.Current` for ASP.NET Core requests, `SpyglassScope.Begin` for workers, `uow`
   records at scope end, `uowId` on every `cmd`.
4. **Callsites**: sampled stack capture after the first per `(uow, shape)` key, `callsite` records with frames;
   the collector's `ICallsiteStore` is ready for them.
5. **Collector side** (03 §5.3): the UoW accumulator and its rules, `n-plus-one` (precise), `cursor-not-exhausted`,
   `pool-starvation`, `client-overhead`; `/uows` and `/uows/{id}`; the UI's unit-of-work page (ops in order,
   timeline, totals) and the "same unit of work" link that the live feed already renders a badge for.
6. **Budget and sample**: the 04 §9 gates in Release tests (dormant / active / sampled CPU and allocation per
   command), and a sample ASP.NET Core service in `samples/` that demonstrates an N+1 and a leaked cursor,
   wired into `docker-compose.yml` next to `DemoApp`.

**Done when.** M3's exit criteria in `docs/spec/README.md` §4: join rate ≥ 99.9 % end to end, exporter
budget green, pool wait, unit-of-work grouping and callsites visible for the sample, dormant cost measured and
written into a `STATUS-M3.md`.

---

## Phase 4 — the load-test workflow

The architecture already degrades correctly under load: storage samples and keeps every slow, errored and
alerted op, aggregates stay complete, ingest sheds with 429. What is missing is the workflow around a test.

### 4.1 Runs

`POST /api/v1/runs {name}` opens a run, `POST /runs/{id}/close` ends it; a run is a named time range with
notes, stored in SQLite. The live feed and shapes page show the current run. Runs are what you compare.

### 4.2 Compare

`GET /api/v1/compare?a=<run|from,to>&b=<run|from,to>` returns, per shape: count, p50/p95/p99, errors in a and
b, the deltas, and whether the shape is new in b or gone from a. The UI gets a compare page: pick two runs or
two ranges, sort by p95 delta, click through to the shape. This is the one page that makes the aggregates
earn their keep for a performance test. It reads the Phase 1.4 hourly sums and minute rows; no new storage.

### 4.3 Thresholds and quiet windows

`slow-shape` is a single global 100 ms. Under a stress test everything crosses it and the alerts page is a
list of every shape. Make thresholds per rule and overridable per namespace and per app (a small table, a
settings page), and add a quiet window: "no new alerts from now until I say so", recorded against the run so
the alerts that would have fired are still counted and shown on the run's page afterwards.

### 4.4 Tell people when storage is sampling

Sampling is visible only in `/healthz` detail. The UI should show a banner on Live and Operation pages when
`storage-backlog` says sampling is on, and the op list should say "n ops not stored in this range". The
counters exist (`Sampled`); surface them.

### 4.5 Export without the cap

`Export` stops at 100,000 ops. Stream by ULID range with no cap, gzip the response, and include the aggregates
for the range so an export is a complete record of a run.

**Done when.** A scripted load test (the `IngestBenchTests` generator against a real proxy) runs twice with a
deliberate regression in between and the compare page shows it in the top row.

---

## Phase 5 — server-side truth

Server latency here is first reply byte minus last request byte at the proxy. It is the right number and the
UI explains it. But the wire cannot show `planSummary`, `keysExamined` or `docsExamined`, and "is it using an
index" is the first question about any slow shape. The spec rules out running commands against the user's
database (README §1.2). Keep that as the default and offer two opt-ins:

* **Explain on demand.** A per-collector, user-supplied read-only connection string (a secret, stored
  encrypted at rest, never shown again). The shape page gets an "explain" button that runs
  `explain({verbosity: "queryPlanner"})` for the slowest example's command, through the proxy so it shows up
  in the feed like anything else, and renders the plan: winning plan stages, index used or `COLLSCAN`.
  Nothing is automatic; the user clicks.
* **Profiler tail.** A sidecar (or a collector option with the same credentials) that reads `system.profile`
  on the upstream, or tails the mongod log's slow-query lines, normalises the command with the same
  `ShapeNormalizer` and joins on shape hash. That gives `planSummary`, `keysExamined`, `docsExamined` and
  `nreturned` per shape, over time, with no per-op join and no proxy involvement. An `index-miss` rule
  (`COLLSCAN` on a collection over N documents) follows from it.

**Done when.** The shape page for a `find` without an index says `COLLSCAN` and names the collection.

---

## Phase 6 — operability

Not a phase so much as a list; pick from it as deployments demand.

* **Alert notifications.** Webhook per rule or severity (generic JSON, Slack-shaped payload as an option), with
  the alert's summary, scope and a deep link. Alerts that nobody is looking at are not alerts.
* **Security posture, documented and defaulted.** The ingest token is advertised to every client in the
  `$spyglass` hello document (`discovery.advertiseToken`, default true), so anyone who can connect to the
  proxy can inject records. Keep it for the exporters' sake, but say so in the README, and make
  `capture.values: shape` the documented recommendation for any environment with production-shaped data.
  Set the session cookie `Secure` when the collector is behind a TLS-terminating proxy
  (`X-Forwarded-Proto`), not only when Kestrel itself is HTTPS.
* **Deployment examples.** Kubernetes manifests: proxy `Deployment` with a `Service` exposing
  `basePort..basePort+n` and `advertiseHost` set to the Service name, collector `StatefulSet` with a PVC,
  Prometheus `ServiceMonitor` for both. A Helm chart once the manifests settle.
* **Multi-cluster.** Decided in Phase 2.
* **Retention knobs in the UI.** Show what retention and sampling are doing; let the admin change retention
  without a restart (write the value to SQLite, read it in `StorageWriter.Retention`).
* **The proxy as a sidecar.** A note and an example on running the proxy in the application's pod on
  `localhost`, which removes the network hop and the TLS question for the client side.

---

## Phase 7 — the Node exporter (M4)

`docs/spec/05-exporter-node.md`, after M3, reusing the collector-side work. The prototype that ran in a browser
during M2 (STATUS-M2 "Node driver 6, in a browser") is the starting point. Timing by the exporter's own clock,
`$spyglass.member` mapping by port, `AsyncLocalStorage` for units of work, `Error.captureStackTrace` for
callsites, sampled. Same exit criteria as M3 for an Express sample and a plain `node:http` sample.

---

## Decided against, so nobody re-opens it by accident

* **A rule engine.** The analyzers are straight-line code with O(1) state per op. The review that led here
  measured what the alternative cost.
* **Production traffic as a target.** The proxy would likely survive it; the design is still staging and
  test environments. If that changes, the first work is a second analysis thread (partition the analysis
  stage by shape hash) and a store that is not SQLite.
* **Running commands against the database by default.** Phase 5 is opt-in and user-initiated.
* **Exporter-only mode.** Context without the wire has nothing to attach to (README §1.2).
