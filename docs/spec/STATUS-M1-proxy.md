# M1 — proxy implementation status

Code: `src/Spyglass.Proxy` (BCL-only, `spyglass-proxy`). Tests: `tests/Spyglass.Proxy.Tests` (82 tests;
real `mongod`/`mongos` 8.0.23 via `tools/mongod/fetch.sh`, .NET driver 3.12, Node driver 6.4.0 /
6.21.0 / 7.7.0 via `tests/node-conformance`). Bench: `tools/ProxyBench`.

```sh
tools/mongod/fetch.sh && (cd tests/node-conformance && npm install)
dotnet build tests/Spyglass.Proxy.Tests && dotnet tests/Spyglass.Proxy.Tests/bin/Debug/net10.0/Spyglass.Proxy.Tests.dll
dotnet build -c Release tests/Spyglass.Proxy.Tests && dotnet tests/Spyglass.Proxy.Tests/bin/Release/net10.0/Spyglass.Proxy.Tests.dll -class Spyglass.Proxy.Tests.AllocationTests
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

`tools/ProxyBench`, 4 vCPU shared by the client (32 concurrent workers), `mongod` and the proxy;
medians of 3 rounds, point lookups by `_id`:

| | +p50 | +p99 | throughput vs direct | 4 MiB reply vs direct | proxy CPU / op | alloc / frame |
|---|---|---|---|---|---|---|
| budget | ≤ 50 µs | ≤ 250 µs | ≥ 90 % | ≤ 110 % | — | 0 B (forwarding) |
| bare (forwarding only) | 31 µs ✅ | 107 µs ✅ | 87 % ❌ | 113 % ❌ | 30 µs | **0.15 B** ✅ (gate test) |
| observe (headless) | 76 µs ❌ | 192 µs ✅ | 83 % ❌ | 115 % ❌ | 42 µs | 129 B (observer) |
| export | 47 µs ✅ | 102 µs ✅ | 77 % ❌ | 114 % ❌ | 53 µs | ~1.4 KB (observer + records) |

Reading it: this box is CPU-saturated by client + mongod alone, so throughput and the 4 MiB ratio
measure proxy CPU more than forwarding latency. Run-to-run noise on `direct` alone is ±15 %.
The budgets are to be judged on the nightly mongod runner with the proxy pinned to its own cores
(06 §4); until then the numbers to drive down are **CPU / op** (bare 30 µs, observer +12 µs,
export +11 µs) and observer allocation.

What moved the numbers (each measured): inline socket completions (+p50 70–104 → 16–46 µs);
pump-local statistics instead of per-read interlocked ops; pooled `ValueTask` builders; a
quadratic pending-queue compaction in the observer (15.9 KB → 0.3 KB per frame); per-worker
`Regex` and metric-child caches; skipping redaction when headless.

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

1. Collector v1 (wire-only, 03): ingest, shapes, analyzers, storage, UI. Not started.
2. Observer CPU: per-op `RequestInfo`/string allocation (129 B/frame), export JSON (≈1.4 KB/frame).
3. Dedicated tests: tap-digest byte transparency, half-close / death mid-reply, pending-at-close.
4. Nightly-only: PLAIN/LDAP, MONGODB-AWS, X.509 documented failure, SRV end-to-end, perf on pinned cores.
5. Delete the old `MongoSpyglass.Proxy` once the collector replaces `MongoSpyglass.Service`.
