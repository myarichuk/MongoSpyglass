# M2 — Ingest Protocol v0 frozen + collector join: status

Exit criteria (README §4): conformance vectors published; the collector joins synthetic exporter
records at ≥ 99.9% on the harness; late, duplicate and orphan records handled per 02 §6.

| Criterion | Status | Evidence |
|---|---|---|
| Vectors (06 §3) | ✅ | `docs/spec/vectors/ingest-v0`: `records/` (full + minimal of all 10 record types), `normalize-address.json` (14 cases), `join/` (17 scenarios), `shape/` (5 files, every 03 §4.1–4.3 rule, the proxy's encodings, 17 grouping pairs) |
| Join ≥ 99.9% on the harness | ✅ 100% | `JoinBenchTests`: 50k ops over HTTP from two producers, cmds 10% early and 0–3 s late, 64 connections; 100.000% joined, 0 conflicts, 0 orphans, in two runs |
| Late, duplicate, orphan (02 §6) | ✅ | `join/` vectors, against the in-memory store and SQLite; each outcome counted (`spyglass_collector_join_total{result}`) |
| Join CPU ≤ 5 µs (03 §11) | ✅ 0.78 µs | per op + cmd pair, engine alone, Release; 95 B allocated per pair |
| Schema 1 frozen (02 §1) | ✅ | with the vectors as its definition |

## Verified on real traffic

* **.NET driver, real proxy, real mongod** (`EndToEndTests`): a minimal exporter built the way 04 says
  (key from `RequestId` and `ConnectionId.LongServerValue`, `proxyId`/`member` from the `$spyglass` in
  the heartbeat, the handshake marker in `LibraryInfo`). Every command joins its op; no conflicts, no
  orphans; each stored op carries its own command's context.
* **Node driver 6, in a browser.** A prototype exporter on the Node driver's events (05 mapping, by port):
  218 of 218 commands joined, 98 in time and 120 merged into stored ops (it flushed only at the end).
  Live feed with client latency and unit-of-work badges, the feed filtered to one unit of work, op detail
  with pool wait, client time, client overhead and callsite frames, and per-exporter join rate and
  clock skew on the producers page.

## What the vectors and real traffic found

1. **Shapes lost key order** where it matters. The canonical form sorts document keys, so `sort {a:1,b:1}`
   and `sort {b:1,a:1}` were one shape; same for index hints and `$sort`. They are now written as
   `[[key, dir], …]` (03 §4.3).
2. **`$project` inclusion flags were tagged**, so `{x:1}` and `{x:0}` grouped. They are kept now.
3. **Auth commands got a valueHash** in the normalizer but not in the pipeline (which stripped them
   first). The normalizer owns the rule now, so both agree.
4. **The proxy wrote `"setName":null`** for a standalone, against 02 §4's "absent means default". Caught
   by checking every record the proxy emits against the vectors.
5. **02 §4.2 was missing fields the proxy sends**: `reply.cursorsKilled`/`cursorsNotFound`,
   `stats.dropped.pending_evicted`. Added before the freeze.
6. **The Node driver's durations are whole milliseconds** (`now()` floors `hrtime`). With `event.duration`
   as `client_us`, a 300 µs command reported 0 or 1 ms, below its own server latency. 05 now has the
   exporter time commands and pool checkouts itself.
7. **Join windows measured from finalization** instead of the op's arrival made a cmd 11 minutes late
   still merge when nothing woke the partition in between. The window now runs from arrival.
8. **Stream replies after storage raced the append** in SQLite (the update went straight to the store,
   possibly before the writer had appended the op). Updates now go through the writer's FIFO.

## Design notes (in 02 §6.2 and 03 §3)

* An op is analysed and live on arrival; only storage waits for the join. Live-feed latency doesn't move
  with exporters, and a joined op is written once.
* Ops don't wait while no exporter has reported for 10 minutes. A wire-only deployment pays nothing
  for the join; a `cmd` that turns up later still merges into the stored op.
* Waiting sets are bounded (200k ops, 200k cmds); the index of stored ops a late cmd can reach is 2^17
  per partition. Overflow is counted, never silent.

## Not in M2

* Units of work (03 §5.3), and the exporter rules that need them (`n-plus-one`, `cursor-not-exhausted`,
  `client-overhead`, `pool-starvation`): `uow` and `pool` records are accepted and counted. Joined ops
  carry `uowId`, and `/ops?uow=` and the live feed filter by it.
* The exporters themselves (04, 05: M3, M4).
