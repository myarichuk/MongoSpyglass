# M1 — proxy implementation status

Code: `src/Spyglass.Proxy` (BCL-only, `spyglass-proxy`). Tests: `tests/Spyglass.Proxy.Tests` (92 tests;
real `mongod`/`mongos` 8.0.23 via `tools/mongod/fetch.sh`, .NET driver 3.12, Node driver 6.4.0 /
6.21.0 / 7.7.0 via `tests/node-conformance`). Bench: `tools/ProxyBench`.

```sh
tools/mongod/fetch.sh && (cd tests/node-conformance && npm install)
dotnet build tests/Spyglass.Proxy.Tests && dotnet tests/Spyglass.Proxy.Tests/bin/Debug/net10.0/Spyglass.Proxy.Tests.dll
dotnet build -c Release tests/Spyglass.Proxy.Tests && dotnet tests/Spyglass.Proxy.Tests/bin/Release/net10.0/Spyglass.Proxy.Tests.dll   # + allocation/cost gates
```

## 06 §2 conformance — where each item is covered

| Item | Status | Test |
|---|---|---|
| 2.1 CRUD/aggregation parity with direct | ✅ | `StandaloneTests.Crud_through_proxy_matches_direct` |
| 2.1 transactions | ✅ | `ResilienceTests.Transactions_commit_and_abort` |
| 2.1 change stream resume after proxy-side reset | ✅ | `ResilienceTests.Change_stream_resumes_after_proxy_side_reset` |
| 2.1 retryable writes after reset | ✅ | `ResilienceTests.Retryable_write_after_reset` |
| 2.1 16 MiB document, ~48 MB of batches | ✅ | `StandaloneTests.Large_document_and_large_batch` |
| 2.1 invalid frame fails closed, others unaffected | ✅ | `StandaloneTests.Invalid_frame_fails_closed_without_affecting_others` |
| 2.1 arbitrary chunking / pipelining | ✅ | `Byte_at_a_time_client_still_works`, `Pipelined_frames_in_one_write_all_answered` |
| 2.1 byte transparency by tap digest; bulk/GridFS; half-close and death mid-reply as dedicated cases | ⏳ | covered indirectly (all suites run through the proxy); dedicated digest test not written |
| 2.2 replica set discovery only through proxy listeners | ✅ | `ReplicaSetTests.Driver_discovers_members_only_through_the_proxy` (asserts no socket to a real member) |
| 2.2 failover | ✅ | `ReplicaSetMutationTests.Stepdown_writes_resume_on_new_primary_through_proxy` |
| 2.2 member added → listener allocated, discovered | ✅ | `ReplicaSetMutationTests.Added_member_gets_a_listener_and_is_discovered` |
| 2.2 sharded (2 mongos) | ✅ | `ShardedTests.Two_mongos_through_two_listeners` |
| 2.2 SRV upstream | ✅ resolver | `SrvTests` (in-test DNS: UDP, TCP on truncation, seedlist rules). End-to-end SRV connect needs resolvable SRV targets: CI item. |
| 2.3 `$spyglass` in hello / heartbeat / streaming replies | ✅ | `StandaloneTests.Hello_reply_carries_spyglass_document`, `ReplicaSetTests`, `NodeDriverTests` |
| 2.3 marker detection | ✅ | `ObservationTests.Exporter_marker_is_detected` (.NET `LibraryInfo`), `NodeDriverTests` (`appendMetadata`) |
| 2.4 upstream TLS, wrong CA fails closed, downstream self-signed TLS | ✅ | `TlsTests` (3-member TLS replica set) |
| 2.4 SCRAM-SHA-1/256 + speculative auth | ✅ | `RedactionTests` (auth-enabled mongod) |
| 2.4 PLAIN (LDAP), MONGODB-AWS, X.509 documented failure | ⏳ | need Enterprise/LDAP and AWS credentials: nightly CI |
| 2.5 unacknowledged writes | ✅ | `ObservationTests.Errors_and_unacknowledged_writes` |
| 2.5 streaming hello → one op + `stream` records | ✅ | `NodeDriverTests` |
| 2.5 connection close with pending requests | ✅ code path; ⏳ dedicated test | |
| 2.6 redaction (records, decoded bodies, metrics, Trace logs) | ✅ | `RedactionTests` (mutation-checked: removing auth commands from the list makes it fail) |
| 2.7 strip / opaque / observe | ✅ | `ReplicaSetTests.Compression_is_stripped_from_the_handshake`, `CompressionTests` |
| join key (02 §3) vs real driver events | ✅ 100% | `ObservationTests.Join_key_from_driver_events_matches_wire_ops` (.NET), `NodeDriverTests` (Node ×3) |

## 01 §14 performance — measured

### End to end (`tools/ProxyBench`)

4 vCPU shared by the client (32 concurrent workers), `mongod` and the proxy; medians of 3 rounds,
point lookups by `_id`. **Read only the CPU column.** On this VM the `direct` baseline alone
swings between 18k and 36k ops/s from round to round, so the throughput and latency ratios
mostly measure the neighbours. With 5 alternating rounds, observe and bare have the same p50
(189 µs vs 189 µs).

| proxy CPU / op | bare | observe | export |
|---|---|---|---|
| M1 first cut | 30 µs | 42 µs (+12) | 53 µs (+23) |
| after the observer pass | 29.6 µs | 31.9 µs (**+2.3**) | 35.0 µs (**+5.4**) |

The budgets (+p50 ≤ 50 µs, +p99 ≤ 250 µs, throughput ≥ 90 %, 4 MiB ≤ 110 %) are to be
judged on the nightly mongod runner, with the proxy pinned to its own cores (06 §4).

### Isolated, deterministic (Release test gates)

* **Forwarding** (`AllocationTests`): a raw-socket client that allocates nothing, so every byte
  counted is the proxy's. Bare forwarding: **0.01–0.02 B/frame** (gate < 1).
* **Observation** (`ObserverCostTests`): driver-shaped frames (find by `_id` with `lsid`, a
  one-document cursor reply) go through the real tap → worker queue → correlation → metrics
  (→ records → sender → HTTP) path, with no sockets or mongod. Measured per op (one request
  plus one reply) and gated on B/op:

| mode | tap (pump thread) | worker | allocation | before this pass |
|---|---|---|---|---|
| headless (metrics) | ~1.7 µs | ~1.4 µs | **0–1 B/op** (gate 4) | 6–9 µs CPU, 350 B/op |
| records built | ~1.5 µs | ~3.3 µs | **0–0.5 B/op** (gate 4) | 8 µs CPU, 1712 B/op |
| records shipped over HTTP | ~1.5 µs | ~3.3 µs | **5–7 B/op** (gate 16): HttpClient per batch | — |

What moved the numbers, each one measured:

* **lsid hashing was ~90 % of observer CPU.** SHA-256 went through the platform provider,
  twice per op (parser and redactor), for values that repeat. Now it's a per-worker cache by
  session id, and headless mode doesn't hash at all.
* **Pumps contended with workers on `Channel<T>`'s lock**, which an unbounded channel takes on
  every write; that was half the tap's cost. Replaced by a lock-free queue and a dedicated
  worker thread.
* **Every frame paid a thread wake on the pump thread**, because below saturation the worker
  is always asleep. Replaced by timer-based worker sleep, with a wake on every 64th publish
  (01 §4).
* **Per-op allocation removed:**
  * `RequestInfo` is pooled per worker;
  * command/db/collection/codeName strings are interned;
  * the redactor writes into a reused writer, with no per-op `StringBuilder` or arrays;
  * field-pattern verdicts are cached per name;
  * records serialize into a reused writer and go out as pooled NDJSON chunks, so there's no
    `byte[]` per record, no batch concatenation and no `List<byte[]>`;
  * JSON property names are pre-encoded, and Release skips JSON structural validation (Debug
    keeps it).
* Earlier: inline socket completions (+p50 70–104 → 16–46 µs), pump-local statistics, pooled
  `ValueTask` builders, and the quadratic pending-queue compaction.

Measurement fixes made along the way, each of which had been producing wrong numbers:

* **Process CPU** charges idle thread-pool spinning to whatever ran. Worker cost now comes
  from `spyglass_proxy_observer_busy_microseconds_total`, a new metric that is also
  operationally useful: its rate is the worker's utilization.
* **Tap cost** is now taken from `CLOCK_THREAD_CPUTIME_ID` on the feeding thread, not wall
  time, which preemption inflated.
* **The gates run in a non-parallel collection,** after disposing clusters leaked by earlier
  tests' `MongoClient`s (the driver keeps them in a process-wide registry).
* **The gates wait out the `EventListener`'s own asynchronous event dispatch** before opening
  the window.
* **The capture pool is filled to its bound** before measuring, because bursty feeding
  otherwise grows it mid-window.

Known residual: of the tap's ~0.75 µs per frame, ~0.4 µs is the cross-core handoff itself
(measured by returning slots on the same thread instead). Removing it needs per-connection
slot caching, which conflicts with the I4 byte bound (idle connections would pin charged
slots). At under 3 % of the per-frame forwarding cost, it isn't worth it now.

## Spec corrections made from measurements

* Node `commandStarted.address` is the resolved socket `IP:port`, not the dialled host →
  exporters send `$spyglass.member`, mapped by port (02 §3.2, 05 §4).
* The .NET driver raises command events for the handshake with `LongServerValue == null` →
  exporters skip them (02 §3.3, 04 §4).
* Dotted-path field names must be redacted by segment (01 §9.2).
* Truncated captures lose `$db`/`lsid`/`txnNumber` (Node 6.x sends `documents` inline) →
  request-side streaming scan of the body (01 §10).
* The .NET driver ignores `tlsCAFile` in connection strings (test infrastructure uses
  `SslSettings`); `MongoClient.Dispose()` does not close the shared cluster's sockets.

## Open for M1

1. ~~Collector v1~~ built: see [STATUS-M1-collector.md](STATUS-M1-collector.md).
2. ~~Record size~~ done: wire records omit absent/default fields and derivable values, and drop
   `proxyId` (it is `src`). 02 §4 has the rules and the derivations. A point-lookup op record
   went from 858 to 497 B; the worker's record-building CPU moved only ~0.2 µs/op, so JSON size
   was a smaller part of export cost than the first estimate said.
3. Dedicated tests: tap-digest byte transparency, half-close / death mid-reply, pending-at-close.
4. Nightly-only: PLAIN/LDAP, MONGODB-AWS, X.509 documented failure, SRV end-to-end, perf on pinned cores.
5. Delete the old `MongoSpyglass.Proxy` once the collector replaces `MongoSpyglass.Service`.
