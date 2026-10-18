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

| | measured | target |
|---|---|---|
| ingest accepted (HTTP + parse + routing) | 57–65k records/s | 50k ✅ |
| analysed (stage 1 + stage 2) | 27–32k records/s | 50k ❌ (this box, client in-process) |
| stored (SQLite, 5 secondary indexes) | ~20k rows/s; sampling (03 §7) takes over above that | — |
| POST → live event, at ~10k records/s | p50 3–5 ms, p95 6–12 ms | ≤ 1 s ✅ |
| collector CPU per record | partitions ~20 µs, analysis ~11 µs, storage ~25 µs, HTTP ~26 µs | — |

What moved the numbers, each one measured:

* **Partition CPU 55 → 20 µs/record.**
  * A per-op LINQ scan in the `repeated-query` state (O(values per connection)) became a FIFO.
  * BSON decode went 11 → 2.5 µs with a direct span decoder instead of `BsonSerializer`.
  * Normalization went 15 → 7 µs with a canonical writer that sorts while it writes, instead of
    a sorted copy plus the driver's JSON writer.
* **Hand-offs:** one queue operation per POST per partition, not per record.
* **No live-feed JSON is built when nobody subscribes.**

The analysis ceiling needs measuring on a machine where the load generator doesn't share the
cores. Storage below ingest rate is designed for (sampling keeps every slow, errored and
alerted op; the aggregates stay complete).

## Deviations from the spec text (recorded in the specs)

* 02 §6.4 dedup is bounded by memory (2^18 key hashes per partition).
* 03 §4 hash input is compact canonical JSON written by the collector (same grouping as the
  driver's sorted relaxed EJSON, which a test pins).
* 03 §5.2: the four rule changes above. The `slow-shape` p95 baseline is the previous whole
  hour (two tumbling sketches per shape), not a sliding 60 minutes.

## Open

1. ~~M2: the join~~: done, `STATUS-M2.md`.
2. Analysis throughput on dedicated cores. The next costs are allocation (~9 KB/record end to
   end) and SQLite writes.
3. A container image (the publish script produces the directory it would copy). Not built here:
   there is no Docker in this environment.
4. One full-suite Release run had one `EndToEndTests` failure whose message was lost. It has
   not reproduced in 9 runs since, nor in 5 standalone. The test names the missing keys if it
   recurs.
