# 05 — Node.js exporter (`@spyglass/mongodb-exporter`)

Optional. It has the same contract as the .NET exporter (04): fail-safe (I5), context never
content (I6), dormant until discovery (02 §2.3).

## 1. Package

| | |
|---|---|
| Name | `@spyglass/mongodb-exporter` |
| Runtime | Node.js ≥ 20.19 (matches `mongodb` 7.x `engines`). It's also tested on Node 22 and 24. |
| Peer dependency | `mongodb` `>=6.4.0 <8`. 6.4.0 is the first release whose command events carry `serverConnectionId` (verified: absent in 6.0.0–6.3.0, present in 6.4.0 and 7.7.0). |
| Optional peer | `@opentelemetry/api` `^1`. Loaded lazily inside `try/catch`, and never required. |
| Runtime dependencies | **none**. It uses `node:http`, `node:https`, `node:zlib`, `node:async_hooks`, `node:diagnostics_channel`, `node:crypto` (UUID only), `node:perf_hooks`. |
| Module formats | ESM + CJS (dual package, `exports` map). Type definitions are bundled. |

## 2. Public API

```ts
import { MongoClient } from "mongodb";
import { instrument, runInUnitOfWork } from "@spyglass/mongodb-exporter";

const client = new MongoClient(uri);
instrument(client, {
  ingest: undefined,             // explicit endpoint; default: discover from $spyglass
  token: undefined,
  appVersion: process.env.npm_package_version,
  callsites: "sampled",          // "off" | "sampled" | "always"
  callsiteSampleRate: 100,
  httpUnitOfWork: true,          // §6.2
  unitOfWorkNamer: undefined,    // (req: IncomingMessage) => string | undefined
});
await client.connect();

// explicit scope for non-HTTP work (queues, cron):
await runInUnitOfWork("ProcessOrderMessage", async () => { … });
```

* `instrument()` MUST be called before `client.connect()` (or the first operation) for the
  handshake marker to appear on all connections. When called later, it still works, but
  connections that already exist carry no marker, which only shortens their join wait
  (03 §3.2).
* Calling it twice on the same client is a no-op.
* It returns `{ shutdown(): Promise<void> }`, which flushes for ≤ 1 s.
* Environment variables override options:
  * `SPYGLASS_INGEST`, `SPYGLASS_TOKEN`;
  * `SPYGLASS_DISABLED=1` (hard off: `instrument()` returns immediately and subscribes to
    nothing);
  * `SPYGLASS_CALLSITES`.

## 3. What `instrument()` does to the client

1. `client.appendMetadata({ name: "spyglass-node", version })`. This is a public API that
   recomputes the handshake metadata and deduplicates equal entries. Verified in
   `mongodb` 7.7.0.
2. `client.monitorCommands = true` (public setter). Command events aren't emitted otherwise.
   CMAP and SDAM heartbeat events are always emitted.
3. Subscribes to the client's events (names verified against `MONGO_CLIENT_EVENTS` in
   7.7.0):

| Event | Used for |
|---|---|
| `serverHeartbeatSucceeded` (`reply`, `connectionId` = server address) | discovery: read `reply.$spyglass`; map address → `proxyId` |
| `commandStarted` (`requestId`, `serverConnectionId`, `address`, `connectionId`, `commandName`, `databaseName`) | start context capture |
| `commandSucceeded` / `commandFailed` (`failure`) | emit `cmd` |
| `connectionCheckOutStarted` (`address`), `connectionCheckedOut` (`address`, `connectionId`) | pool wait (timed by the exporter, see below) |
| `connectionCheckOutFailed` (`reason`, `durationMS`) | `pool.checkoutFailed` |
| `connectionPoolCreated` (`options.maxPoolSize`), `connectionCreated`, `connectionClosed`, `connectionCheckedIn` | `pool` statistics |

**Durations are the exporter's own.** The driver's `duration` (command events) and `durationMS` (CMAP
events) come from its `now()`, which floors `process.hrtime()` to whole milliseconds (6.4.0, 6.x, 7.7.0,
`lib/utils.js`). A 300 µs command reports 0 or 1 ms: `client_us` below the server latency, and
`clientOverhead_us` meaningless. The exporter reads `process.hrtime.bigint()` itself, in the handlers of
`commandStarted` and `commandSucceeded`/`commandFailed` (`client_us`), and of
`connectionCheckOutStarted` and `connectionCheckedOut` (`poolWait_us`, keyed by `address` while the
checkout is pending). The handlers run synchronously where the driver emits, so they bracket the same
interval. Found by driving the collector UI with a prototype exporter (M2).

Setting `monitorCommands` makes the driver build event objects (including a copy of the
command document) for every command. That cost belongs to the driver and is reported
separately from the exporter's own budget (§9).

The exporter never reads `event.command`. From `commandSucceeded.reply` it reads only
`cursor.id`, for `cmd.cursorId` (02 §4.3).

## 4. Join key construction

| Key part | Source |
|---|---|
| `member` | the `$spyglass.member` of the listener, found **by port**: `event.address` is the socket's resolved `IP:port`, not the dialled host (02 §3.2). Ambiguous ports are resolved with `dns.lookup` of the heartbeat hosts. |
| `proxyId` | latest `$spyglass.proxyId` from the heartbeat reply of that listener; else `null` |
| `serverConnectionId` | `event.serverConnectionId` (`bigint \| null`), converted with `Number()`. If the value exceeds `Number.MAX_SAFE_INTEGER`, emit it as a string (not expected in practice). |
| `requestId` | `event.requestId`. The driver takes it from the module-level counter it also writes into the wire header (`lib/cmap/commands.js`). |

`operationId`: the Node driver doesn't expose an operation id on command events, so emit
`null`. Retries are detected collector-side by `(member, command, uowId, adjacent ts)` and
aren't attributed in v1.

## 5. Pool wait

The Node `connectionCheckedOut` event has no operation id, but a checked-out connection
serves exactly one operation until it's checked back in. So:

* On `connectionCheckOutStarted`, push `process.hrtime.bigint()` on a per-`address` queue. On
  `connectionCheckedOut`, pop the oldest and store the elapsed µs in a `Map` keyed by
  `` `${address}|${connectionId}` `` (not `durationMS`: whole milliseconds, §3).
* On the next `commandStarted` with the same `address` and `connectionId`, take and delete
  it as `poolWait_us`.
* Subsequent commands in the same checkout (for example, batches of a split bulk write) get
  `poolWait_us = 0`.
* On `connectionCheckedIn`, delete any leftover entry.

`pool` statistics keep counters (`total`, `inUse` = checked out − checked in, `waitQueue` =
check-out started − (checked out + failed)) and a 64-bucket log2 histogram of `durationMS`
per address.

## 6. Unit of work

`AsyncLocalStorage<UowContext>`, a module singleton.

### 6.1 Resolution order at `commandStarted`

1. A `runInUnitOfWork` scope: `{ id: ULID, name, kind: "scope" }`.
2. An active OpenTelemetry span (when `@opentelemetry/api` resolved): the root span name of
   the local trace. `id` = traceId; `trace.traceparent` is built from the active span
   context.
3. An HTTP UoW (§6.2).
4. Otherwise `null`.

**Async context.** Command events are emitted synchronously inside the async chain started
by the user's call. `AsyncLocalStorage` propagates through promise continuations, including
waiting in the pool's wait queue, because the waiter resumes in the context where it began
awaiting. The conformance test 06 §2.5 MUST assert this for each supported driver major,
including the queued-checkout case.

### 6.2 Automatic HTTP unit of work

When `httpUnitOfWork` is true and the exporter is active:

* Subscribe to `diagnostics_channel` `http.server.request.start` (available since Node
  18.7). In the subscriber, call
  `als.enterWith({ id: ULID, name: namer(request), kind: "http", start: performance.now() })`.
  The message is published synchronously just before the server emits `'request'`, so the
  handler and all its async continuations inherit the store. Verified on Node 22.22: the
  store is visible synchronously in the handler and after `setTimeout`/`await`
  continuations, with no bleed between concurrent requests.
* Subscribe to `http.server.response.finish` to emit the `uow` record (duration, status,
  command count).
* Default namer: `` `${method} ${normalizePath(pathname)}` ``, where `normalizePath`
  replaces these path segments with `:id`:
  * decimal integers;
  * UUIDs;
  * 24-hex ObjectIds;
  * ≥ 16-char base64url tokens.
* Frameworks with route templates can supply better names. A helper for Express
  (`spyglassExpress()`, middleware that renames the current UoW to `req.route.path` on
  `'finish'`) is part of the package. Fastify gets a plugin with the same behaviour.

If `enterWith` propagation turns out to be unreliable under a given framework (the
conformance test fails), the fallback is the explicit middleware. That middleware calls
`als.run()` around `next()`.

## 7. Callsites

V8's zero-cost async stack traces include `at async <fn>` frames for awaited calls. So
`new Error().stack` at `commandStarted` usually *does* reach application frames, unlike .NET
(04 §7).

* Capture only when sampled: the first occurrence per `(uow name, commandName, databaseName)`,
  then 1/`callsiteSampleRate`, or always.
* Temporarily raise `Error.stackTraceLimit` to 50, then restore it.
* Parse with a fixed regex for V8 frame lines.
* Drop frames whose file contains `/node_modules/`, starts with `node:`, or belongs to this
  package.
* Keep ≤ 16 frames. Make file paths relative to `process.cwd()`.
* Source maps: if `process.sourceMapsEnabled` is true, V8 already maps the frames. The
  exporter does no source-map work itself.
* Hash: FNV-1a 64 as in 04 §7 (item 3) (implemented with `BigInt` arithmetic, or two 32-bit
  halves).

## 8. Runtime model

* **Handlers.** Every listener body is wrapped in `try/catch`. Errors increment a counter.
  After 1,000 errors in 60 s, the exporter removes all its listeners and sets
  `monitorCommands` back to the value it had before `instrument()`.
* **Buffering.** Records are plain objects pushed into a bounded array (default 20,000
  records or 4 MiB estimated). Overflow drops and counts.
* **Sender.**
  * A `setInterval(flush, 1000).unref()`, so it never keeps the process alive.
  * Serialization uses `JSON.stringify` per record joined by `\n`, gzip via
    `zlib.gzip` (async) when the batch is ≥ 8 KiB.
  * `http(s).request` with a dedicated keep-alive `Agent` (`maxSockets: 2`), timeout 5 s.
  * Status handling per 02 §5. `stats` every 10 s.
* **Dormant mode.** Listeners are attached (they're needed to see heartbeats), but command,
  pool and HTTP handlers return after one boolean check. No `diagnostics_channel`
  subscription until active. No timers until active.
* **Shutdown.** `beforeExit` triggers a best-effort flush. `SIGTERM` is the application's
  business; `shutdown()` is provided for it.

## 9. Budget (enforced by 06 §4)

Measured per command with paired runs (plain `instrument`-less client vs instrumented, both
with `monitorCommands` on, so the driver's own event cost is excluded) against the reference
fake:

| Mode | Added CPU | Added allocation |
|---|---|---|
| Dormant | ≤ 0.5 µs | 0 objects |
| Active, no callsite | ≤ 8 µs | 1 record object |
| Active, sampled stack capture | ≤ 80 µs on the sampled command | — |

"Driver event overhead" (`monitorCommands=false` vs `true`) is measured and published next
to these numbers, so users can see the full cost.

## 10. Tests

* **Unit** (`node:test`):
  * address normalization vectors;
  * the pool-wait pairing state machine;
  * `normalizePath`;
  * the frame parser over captured V8 stacks (Node 20, 22, 24);
  * the dormant path (no timers, no allocations: checked with `--expose-gc` and heap
    snapshots).
* **Integration** (06 §2): Express sample, Fastify sample and plain `node:http` sample →
  proxy → reference fake and a real mongod. Assert that:
  * join rate ≥ 99.9%;
  * the UoW name is `GET /orders/:id` (Express: `/orders/:id` via the helper);
  * pool contention is visible (`maxPoolSize=2`, 20 concurrent requests);
  * an undisposed cursor (iterate one document of a 500-document `find`, then drop the
    cursor) raises `cursor-not-exhausted`;
  * ALS context survives a queued pool checkout.
* **Failure:** same matrix as 04 §10.
