# M1 — collector implementation status (wire-only)

Code: `src/Spyglass.Collector` (server, `spyglass-collector`) and `src/Spyglass.Collector.Ui`
(Blazor WebAssembly SPA). Tests: `tests/Spyglass.Collector.Tests` (65). Build both with
`tools/collector/publish.sh [out]`; run `dotnet <out>/spyglass-collector.dll`.

```sh
SPYGLASS_TOKEN=… SPYGLASS_ADMIN_PASSWORD=… dotnet artifacts/collector/spyglass-collector.dll --bind 0.0.0.0:8080 --data /data
spyglass-proxy … --collector.endpoint http://collector:8080/ingest/v0 --collector.token …
```

## What is built, against 03

| 03 | Status | Notes |
|---|---|---|
| §1 pipeline | ✅ | HTTP ingest → stage 1 partitions by `(proxyId, member, serverConnectionId)` → stage 2 analysis thread → storage writer. Bounded ingest queue (256 MiB; 429 above 90 %). One hand-off per ingested batch per partition. |
| §2 validation | ✅ | required fields and JSON types per 02 §4; unknown types ignored, unknown fields kept (`extra`); future `ts` clamped and counted per src |
| §3 join | ✅ M2 | see `STATUS-M2.md` |
| §4 shapes, valueHash | ✅ | all §4.1–§4.4 rules; understands the proxy's encodings (`values: shape` tags, truncated strings, `{$binData}` stubs, capped arrays with `{$more}`) |
| §5.2 wire-only rules | ✅ | slow-shape (threshold + p95 regression), n-plus-one-suspected, repeated-query, unbounded-scan, large-result, unbounded-in, cursor-abandoned, error-burst, unacknowledged-writes. Exporter rules: M2/M3. |
| §5.4 cursors | ✅ | keyed by `(proxyId, member, cursorId)` across connections; exhausted / killed / server-expired / timed-out; getMore inherits the opening shape |
| §6 aggregates | ✅ | `(shapeHash, minute)` with a DDSketch (1 %); deltas merged into the store; hourly rollup after 48 h |
| §7 storage | ✅ | SQLite (WAL); ops stored as their NDJSON line and re-read through the parser; retention (24 h) and a 5 GiB size bound; writes batched; sampled under backpressure |
| §8 API | ✅ | everything but the UoW endpoints (M3); SSE live feed with filters |
| §9 security | ✅ | ingest bearer token; password (Basic or login form → HttpOnly SameSite=Strict cookie), trusted header, or none on loopback only; redacted-body tripwire; export header names each proxy's redaction mode |
| §10 UI | ✅ | Live, Operation, Shapes (+ detail), Alerts, Cursors, Topology & producers, login; repo theme, light and dark; fonts bundled; CSP without inline script; keyboard navigation in tables. Units of work: M3. |

## Verified

* **End to end:** the .NET driver → proxy → collector against real mongod
  (`EndToEndTests`). Every command the driver reports (handshakes aside, 02 §3.3) is exactly
  one stored operation, with matching command, db and proxyId. Getmores share the opening
  find's shape, the cursor closes as exhausted with 1500 documents, the N+1 loop opens its
  alert, and the duplicate key counts as failed.
* **In a browser:** Chromium (Playwright) against mongod → proxy → collector with a Node driver
  workload. Sign-in, every page with real data, opening an operation from the live feed,
  mute/unmute, light and dark themes.
* **Mutation-checked:** rule tests (6 mutations caught), sender and store tests, and the
  pipeline suite run against both the in-memory and SQLite stores.
* **Differential tests against the driver as oracle:** the fast BSON decoder (5000 random
  documents of every type, plus 5000 damaged inputs that must fail only with `FormatException`)
  and the canonical form (groups exactly like the driver's sorted relaxed EJSON).

## Rules: what real drivers changed (03 §5.2 notes)

Driving the rules with the Node driver turned up four defects that unit tests written from
the spec had not:

1. **`repeated-query` scoped by value** opened one alert per repeated value: 48 alerts for one
   loop. It is now scoped by shape.
2. **`error-burst` counted only `ok:0`.** A duplicate key is `ok:1` with `writeErrors`, so 75 of
   78 failing inserts raised nothing. Write errors and write-concern errors now count as
   failures, in the aggregates too.
3. **`unbounded-scan` never fired.** It needs a collection that has "returned > 1000 docs in
   one op", and the Node driver caps getMore batches at 1000. Cursor totals now count.
4. **`slow-shape` flagged the streaming hello**, which holds for `maxAwaitTimeMS` by design.
   Ops that wait by design (hello family, tailable/awaitData, change streams and their
   getMores) are now exempt, and stay out of the latency sketches.

## 03 §11 performance (4 vCPU shared with the load generator; `IngestBenchTests`, Release)

| | first M1 build | after the post-M2 pass | target |
|---|---|---|---|
| ingest accepted (HTTP + parse + routing) | 57–65k records/s | 70–89k records/s | 50k ✅ |
| analysed (stage 1 + stage 2) | 27–32k records/s | 42–53k records/s | 50k (on dedicated cores: below) |
| stored (SQLite, 5 secondary indexes) | ~20k rows/s | 31–38k rows/s; sampling (03 §7) above that | — |
| POST → live event, at ~10k records/s | p95 6–12 ms | p95 9–17 ms | ≤ 1 s ✅ |
| allocated per record | 8.7 KB | 1.7 KB | — |
| GC pause during the run | ~800 ms | ~200 ms | — |
| join (engine, per op + cmd) | — | 0.78 µs, 95 B | ≤ 5 µs ✅ |

Per-record CPU, measured stage by stage in isolation (`PartitionCostTests`, `ShapeCostTests`):
stage 1 ~6 µs (of which shape + value from the BSON bytes 3.3 µs, from 10.6 µs decoding a tree),
parse + route 2–3 µs, analysis ~7 µs, storage ~13 µs.

**This machine slows the stages down when they run together.** Stage 1 costs ~6 µs a record alone,
or beside any one other stage, and ~19 µs when ingest, stage 1 and analysis all run at once.
Ruled out by experiment:

* GC: no collections during the measurement.
* Shared counters: batching them changed nothing.
* CPU time: ALU-only burner threads leave it alone.

The normalizer alone slows 2.7× beside two memory-streaming threads (3.4 → 9.3 µs), so the VM's
vCPUs share caches or memory bandwidth, and the stages move enough memory to do this to each
other. Cutting allocation 5× was the lever on our side. The throughput ceiling on dedicated
cores is still to be measured; the isolated costs put stage 1 at ~170k records/s per partition.

What moved the numbers, each one measured (first M1 build, then this pass):

* **Partition CPU 55 → 20 µs/record (M1).** A per-op LINQ scan became a FIFO; BSON decode 11 → 2.5 µs with a
  span decoder; normalization 15 → 7 µs with a canonical writer that sorts while it writes.
* **Shapes straight from the BSON bytes**: no document decoded, no shape tree built. Shape + value 10.6 → 3.3 µs,
  4.7 KB → 0.1 KB. Differential-tested against the tree normalizer (now the tests' oracle) on 20,000 random
  commands; the test caught one divergence in the rewrite before it shipped.
* **SQLite rows bound through SQLitePCL** on a statement prepared once: Microsoft.Data.Sqlite allocated a
  binder per parameter per row (1.2 KB a row). Storage ~23 → ~13 µs a row.
* **Spin-free hand-offs**: `BlockingCollection` spins before it blocks; a stage that keeps up spun more as it got
  cheaper. Replaced by a queue and an event that does not spin.
* **Live-feed JSON only for a subscriber whose filter takes the op** (an open UI tab filtered to one collection was
  serializing every op).
* **Smaller items**: pooled request bodies (a 1 MiB LOH allocation per POST), `cmd` kept as base64 in the line
  and decoded into a reused buffer, a per-record closure in the producer registry, collection-expression arrays
  per call, ULIDs from a counter (the crypto RNG was 1.3 µs per op), cached shape-hash strings, shape JSON only for
  shapes a partition has not sent yet.

Storage below ingest rate is designed for (sampling keeps every slow, errored and alerted op; the
aggregates stay complete).

## Deviations from the spec text (recorded in the specs)

* 02 §6.4 dedup is bounded by memory (2^18 key hashes per partition).
* 03 §4 hash input is compact canonical JSON written by the collector (same grouping as the
  driver's sorted relaxed EJSON, which a test pins).
* 03 §5.2: the four rule changes above. The `slow-shape` p95 baseline is the previous whole
  hour (two tumbling sketches per shape), not a sliding 60 minutes.

## Open

1. ~~M2: the join~~: done, `STATUS-M2.md`.
2. Analysis throughput on dedicated cores: allocation is down to 1.7 KB/record and every stage is
   measured in isolation (above). The number that remains is the ceiling on hardware that does not
   slow co-running stages down.
3. ~~A container image~~: `src/Spyglass.Collector/Dockerfile` (03 §12). Its build stage was replayed
   command for command and its output run as the container runs it; `docker build` itself still needs a
   machine with Docker.
4. ~~The one-off `EndToEndTests` failure~~. Its message was lost, so it can't be proven. The
   test's one timing-dependent assertion is the `n-plus-one-suspected` alert: 10 finds back to
   back, each starting within 5 ms of the previous reply. Measured under load (6 CPU burners on
   4 vCPUs), most gaps were 0.5–1 ms, but spikes reached 4.5 ms, and the first iteration took up
   to 12 ms. One gap over 5 ms mid-loop leaves no run of 10. The test now uses a 50 ms gap, since the
   5 ms threshold is the rule tests' business, and prints the measured gaps if it ever fails. The
   other candidate, an op analysed before its `conn` record, can't happen: the proxy writes
   `conn open` from the same worker before the first op, its sender posts sequentially, and
   ingest applies `conn` records synchronously. Since then: 6 full collector runs (3 Debug,
   3 Release with the benches) and the proxy suite in both, green.
