# 06 — Testing, conformance and performance gates

The current codebase's main failure was shipping features that had never run against a
driver (review §0). This document makes end-to-end verification part of the definition of
done: **no milestone exits without the suites below green in CI.**

## 1. Backends

| Backend | Used for | Notes |
|---|---|---|
| **Mongo.Fakes** (NuGet `Mongo.Fakes.Server`, separate process) | fast inner-loop e2e, perf A/B ratios, exporter join tests | Gaps vs mongod are in review Appendix A. Items 1 (real cursors), 2 (streaming hello), 3 (global request ids), 5 (`failCommand`) and 6 (performance) are **prerequisites** for the suites marked ‡ below. Track them as Mongo.Fakes issues. |
| **mongod 7.0 and 8.0** (Docker, CI) | authoritative proxy conformance: topology, TLS, auth, compression | 3-member replica set with TLS (test CA), a sharded cluster (1 config RS + 2 shard RS + 2 mongos), and a standalone |
| **Recorded wire traces** (`tests/wire-traces/*.pcapng` → frames) | scanner/rewriter regression tests without a server | Captured from the mongod suites. Credentials removed. |

## 2. Proxy conformance suite (M1 gate)

Each test runs with the **.NET driver (3.12, latest 3.x)** and the **Node driver (6.4,
latest 7.x)**, both through the proxy.

### 2.1 Forwarding

* CRUD, aggregation, transactions, change streams (`watch` with resume after a proxy-side
  connection reset), bulk writes, GridFS, retryable writes after an injected upstream reset.
  Results MUST be identical to running direct.
* Byte transparency:
  * every non-handshake frame read by the proxy from one side equals the frame written to
    the other side (tap-level digest comparison);
  * handshake frames differ only by R1–R3 (structural diff).
* Large messages: a 16 MiB document insert and a 48 MB batch reply. No full-frame buffering:
  proxy RSS stays under the configured bound plus 10%.
* Invalid frame (length 3, length 2³¹−1): both sockets close, `invalid_frame` is recorded,
  and other connections are unaffected.
* Half-close propagation; upstream death mid-reply.

### 2.2 Topology

* Replica set:
  * the client uses `mongodb://localhost:<basePort>/?replicaSet=rs0`;
  * it discovers all members via R1, and the client never opens a socket to a real member
    (asserted with a network namespace that blocks direct access);
  * step-down / failover: writes resume on the new primary through the proxy;
  * adding a member (`rs.add`) allocates a listener, and the client discovers it.
* Sharded: two mongos listeners; reads and writes balanced by the driver.
* SRV upstream: an in-test DNS server serving SRV + TXT records.

### 2.3 Discovery and marker

* `$spyglass` is present in hello replies, including streaming-hello (`moreToCome`) replies
  ‡.
* Both drivers connect and operate normally with the extra field.
* The marker is detected for `instrument()`ed and `AddSpyglass()`ed clients, and absent
  otherwise.
* `LibraryInfo` composition preserves a pre-existing ODM `LibraryInfo`.

### 2.4 TLS and auth

* Upstream TLS with a test CA. Wrong CA fails closed.
* Downstream TLS with a self-signed certificate plus `tlsCAFile`.
* SCRAM-SHA-1, SCRAM-SHA-256 and speculative authentication succeed. PLAIN against an
  OpenLDAP container (mongod Enterprise image, nightly only). MONGODB-AWS (nightly,
  credentials from CI secrets).
* X.509 is attempted, fails, and is documented.

### 2.5 Correlation ‡

* Unacknowledged writes (`w:0`) produce `status=unacknowledged` ops.
* Exhaust getMore (where the driver uses it) and streaming hello produce one op plus
  `stream` records.
* Pipelined requests on one connection, if any driver issues them.
* Connection close with in-flight requests yields `connection_closed` ops.
* Node ALS propagation through queued pool checkout (05 §6.1).

### 2.6 Redaction

For every entry in 01 §9.1 and §9.2, plus `speculativeAuthenticate`: grep **all** of the
following for the plaintext password, token and lsid UUID used in the test:

* collector storage;
* the export file;
* the SSE stream;
* the proxy's logs at Trace level.

Zero hits allowed.

### 2.7 Compression

* `strip`: client asks for `zstd,snappy,zlib`; the server-side `hello` reply shows no
  compression.
* `observe` + zlib: facts are extracted from OP_COMPRESSED.
* `opaque`: sizes only, and no failures.

## 3. Ingest Protocol v0 conformance vectors (M2 gate)

Directory `docs/spec/vectors/ingest-v0/` (created as part of M2):

| File | Contents |
|---|---|
| `records/*.ndjson` | One valid example of every record type, with all optional fields set, and a minimal variant. Producers' encoders MUST round-trip them (parse → encode → byte-equal after canonical key order). |
| `normalize-address.json` | `[input, expected]` pairs for 02 §3.2: case, IPv6, missing port (default 27017), trailing dot. Both exporters and the proxy run them. |
| `join/*.json` | Scenarios: interleaved `op`/`cmd` arrival orders, late `cmd` (merge into stored op), duplicate `cmd`, conflicting `command` (no join), `proxyId:null` orphan, proxy restart (new proxyId) with reused serverConnectionId. Each file holds `input` (records in arrival order with arrival times) and `expected` (stored operations after quiescence). |
| `shape/*.json` | Commands → expected `shape` and `shapeHash` (03 §4), covering every row of 03 §4.3 and every operator rule of 03 §4.2. |

The formats, as built in M2:

* `join/*.json`: `{description, input: [{at_ms, record}], expected: {ops: [{proxyId, member,
  serverConnectionId, requestId, joined: {src, operationId} | null, replyCount?}], counters?}}`.
  `at_ms` is the arrival time on the collector's clock. The runner ingests each record at its time,
  drains every stage, then runs the clock a minute past the last record. `ops` is the complete set
  of stored operations. `counters` names the 02 §6 outcomes that must match (`joined`, `joined_late`,
  `conflict`, `orphan_no_key`, `orphan_expired`, `late_discarded`, `already_joined`, `duplicate`).
  The collector runs every scenario against the in-memory store and SQLite.
* `shape/*.json`: `{description, cases: [{name, rule, command, db, coll, cmd | cmdHex, values?,
  cmdTruncated?, expected: {shape, shapeHash, valueHash, inCardinality}}]}`. `cmd` is Extended JSON;
  `cmdHex` is raw BSON for what Extended JSON cannot express (`{$regex: "…", $options: "…"}` reads
  as a regex literal). `shape` is the canonical form as a JSON tree: serialized compactly, it is the
  hashed bytes. `grouping.json` lists pairs that must share a `shapeHash` or must not.
  `SPYGLASS_WRITE_SHAPE_VECTORS=1` fills `expected` from the implementation; a new case is read
  against 03 §4 before it is committed.
* `records/<type>.ndjson`: line 1 is the full example, line 2 the minimal one.

## 4. Performance gates

The A/B harness is the evolution of `tools/ReviewHarness`:

* real driver, paired runs, fixed seeds;
* results written as JSON, with a gate script that compares them against budgets;
* run on every PR (fake backend, ratios only) and nightly (mongod backend, absolute
  numbers);
* a regression > 10% vs the last nightly on any budget line fails the build.

| Gate | Budget source |
|---|---|
| Proxy added latency p50/p99, throughput ratio, 4 MiB reply ratio, 0 B/frame forwarding allocation | 01 §14 |
| Proxy RSS under 10k connections / 20k ops/s | 01 §13 worst-case formula + 10% |
| Collector ingest rate, live latency, join CPU | 03 §11 |
| .NET exporter per-command CPU and allocation (dormant / active / sampled) | 04 §9 |
| Node exporter per-command CPU and allocation | 05 §9 |
| Join rate | ≥ 99.9% at 5k ops/s with both exporters |

**Fake-backend caveat.** Until Mongo.Fakes Appendix A item 6 (performance) lands, the fake's own
~1.2 ms per point lookup inflates the denominator. On the fake backend, gates are enforced
on *absolute added µs* (instrumented − plain), not on ratios. Ratios are enforced only on
the mongod nightly.

## 5. CI matrix

| Axis | Values |
|---|---|
| OS | linux-x64 (all suites), linux-arm64 (proxy perf nightly), windows-x64 (exporters unit tests only) |
| .NET | 8, 10 |
| .NET driver | 3.0 (floor probe), 3.12, latest 3.x |
| Node | 20.19, 22, 24 |
| Node driver | 6.4.0 (floor), latest 6.x, latest 7.x |
| mongod | 7.0, 8.0 (replica set + TLS; sharded; standalone) |

The floor-probe jobs exist to *establish* the minimum supported driver versions stated in
04 §1 and 05 §1. If a floor job fails, the floor is raised in the spec, not the job removed.
