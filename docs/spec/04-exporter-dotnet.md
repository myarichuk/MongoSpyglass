# 04 — .NET exporter (`Spyglass.MongoDb.Exporter`)

Optional. It adds context (unit of work, pool wait, client-side duration, callsite) to
operations the proxy already sees. It obeys I5 (fail-safe) and I6 (context, never content).

## 1. Package

| | |
|---|---|
| Package id | `Spyglass.MongoDb.Exporter` |
| Targets | `net8.0`, `net10.0` |
| Dependencies | `MongoDB.Driver` (≥ 3.0; the floor is confirmed by the CI matrix in 06 §5, and the API surface used here is verified on 3.12) |
| Not dependencies | OpenTelemetry, any JSON library (it uses `System.Text.Json`), any HTTP library (it uses `HttpClient`/`SocketsHttpHandler`) |
| Relationship to `prometheus-net.Contrib.MongoDb` | Separate package. They may share internal code (event pooling, cursor id extraction). Neither depends on the other. |

## 2. Public API

```csharp
var settings = MongoClientSettings.FromConnectionString(cs);
settings.AddSpyglass();                         // all defaults; dormant until discovery (02 §2.3)
settings.AddSpyglass(o =>
{
    o.Ingest = null;                            // explicit endpoint; default: discover from $spyglass
    o.Token = null;
    o.ApplicationVersion = typeof(Program).Assembly.GetName().Version?.ToString();
    o.Callsites = CallsiteMode.Sampled;         // Off | Sampled | Always
    o.CallsiteSampleRate = 100;                 // 1-in-N after the first per key
    o.EnsureRequestActivities = true;           // §6.2
    o.UnitOfWorkNamer = null;                   // Func<Activity, string?> override
});
var client = new MongoClient(settings);
```

```csharp
// Explicit unit of work where there is no Activity (workers, jobs, consumers):
using (SpyglassScope.Begin("ProcessOrderMessage"))
{
    …
}
```

* `AddSpyglass` is idempotent per settings instance.
* It composes with any existing `ClusterConfigurator` (calls it first) and any existing
  `LibraryInfo` (02 §2.4).
* Environment variables override options:
  * `SPYGLASS_INGEST`, `SPYGLASS_TOKEN`;
  * `SPYGLASS_DISABLED=1` (hard off: no subscriptions at all);
  * `SPYGLASS_CALLSITES=off|sampled|always`.

## 3. Driver events used

All are subscribed through `ClusterBuilder.Subscribe<T>`:

| Event | Used for |
|---|---|
| `ServerHeartbeatSucceededEvent` (`Reply`, `ServerId`) | discovery: read `$spyglass` (02 §2.2); map server endpoint → `proxyId` |
| `CommandStartedEvent` (`RequestId`, `OperationId`, `ConnectionId`, `CommandName`, `DatabaseNamespace`, `Timestamp`) | start context capture |
| `CommandSucceededEvent` / `CommandFailedEvent` (`Duration`, `Failure`) | emit `cmd` |
| `ConnectionPoolCheckedOutConnectionEvent` (`Duration`, `OperationId`, `ConnectionId`) | pool wait |
| `ConnectionPoolCheckingOutConnectionFailedEvent` (`Reason`, `Duration`) | `pool.checkoutFailed` |
| `ConnectionPoolOpenedEvent` (`ConnectionPoolSettings.MaxConnections`), `ConnectionPoolAddedConnectionEvent`, `ConnectionPoolRemovedConnectionEvent`, `ConnectionPoolCheckedInConnectionEvent` | `pool` statistics |

The exporter never reads `CommandStartedEvent.Command` or `CommandSucceededEvent.Reply`,
except `Reply` on `getMore`/`find`/`aggregate`, and then only `cursor.id`. That's needed for
`cursor-not-exhausted` (03 §5.2): the exporter sets `cmd.cursorId` (02 §4.3) when a
UoW-associated reply opens a cursor.

## 4. Join key construction

| Key part | Source |
|---|---|
| `member` | `ConnectionId.ServerId.EndPoint`. For `DnsEndPoint`: `Host` lower-cased + `Port`. For `IPEndPoint`: normalized per 02 §3.2. |
| `proxyId` | the latest `$spyglass.proxyId` seen in a heartbeat reply for that `ServerId`. `null` if none. |
| `serverConnectionId` | `ConnectionId.LongServerValue` (null → `null`) |
| `requestId` | `CommandStartedEvent.RequestId` |

`operationId` (`long?`) is emitted as a decimal string.

## 5. Pool wait

* On `ConnectionPoolCheckedOutConnectionEvent`, store `Duration` in a small
  `ConcurrentDictionary<(long operationId, ConnectionId), long ticks>`.
* On the next `CommandStartedEvent` with the same `OperationId` and `ConnectionId`, take
  (and remove) the value as `poolWait_us`.
* Entries older than 60 s are swept on the 10 s `pool` timer.
* If `OperationId` is null, `poolWait_us = null`.

The `pool` record aggregates `poolWait_us` p50/p95/max per server with a fixed-size log2
histogram (64 buckets). There's no allocation per sample.

## 6. Unit of work

### 6.1 Resolution order at `CommandStartedEvent`

The event is raised on the operation's async flow, so `AsyncLocal`/`Activity.Current` are
those of the calling code.

1. `SpyglassScope.Current` (AsyncLocal) → `uow = {id: scope.Id (ULID), name, kind: "scope"}`.
2. `Activity.Current` → walk to the root (`Parent` chain; stop at the first activity whose
   `Kind == Server`, or at the root).
   * `id` = `TraceId` hex.
   * `kind` = `"http"` if the activity is from `Microsoft.AspNetCore`, else `"activity"`.
   * `name` = the `UnitOfWorkNamer`, else `"<http.request.method> <http.route>"` from tags,
     else `DisplayName`.
   * `trace.traceparent` = `Activity.Current.Id`, when W3C format.
3. Otherwise `uow = null`.

### 6.2 Ensuring request activities

ASP.NET Core creates the per-request `Activity` only when something listens. With
`EnsureRequestActivities=true` and **only while active** (not dormant), the exporter
registers an `ActivityListener`:

* `ShouldListenTo = source.Name == "Microsoft.AspNetCore"`;
* `Sample = (ref _) => ActivitySamplingResult.PropagationData`.

That's the cheapest mode that still creates the activity, with no tag collection forced. If
the application already uses OpenTelemetry, its sampler wins, because `PropagationData` is
the minimum and the effective result is the max over listeners.

The exporter emits a `uow` record when the request activity stops. It needs
`ActivityStopped` on the same listener, and only reads `Duration`, the
`http.response.status_code` tag, and the command count it accumulated.

## 7. Callsites

**Limitation (documented, not hidden).** `CommandStartedEvent` fires deep inside the
driver's async pipeline, after connection checkout. `new StackTrace()` at that point shows
only the frames since the last `await` resumed, which usually means driver and thread-pool
frames, with no application frames. So:

1. **Primary:** capture at the *operation entry* via `AsyncLocal` breadcrumbs.
   * The exporter provides `IMongoCollection<T>` decorator extensions:
     `collection.WithSpyglassCallsite()`, returning a wrapping collection. It records
     `[CallerFilePath]`, `[CallerLineNumber]` and `[CallerMemberName]` of each call into an
     AsyncLocal slot consumed at `CommandStarted`.
   * This costs nothing at runtime (compile-time constants) but is opt-in.
2. **Secondary (automatic):** when callsite mode ≠ Off and the event is sampled
   (first occurrence per `(uow name, command, db)`, then 1/`CallsiteSampleRate`):
   * capture `new StackTrace(skipFrames: 1, fNeedFileInfo: true)`;
   * drop frames whose declaring type namespace starts with `MongoDB.`, `System.` or
     `Microsoft.`;
   * if ≥ 1 frame remains, emit it as a callsite. Otherwise emit none (`callsite: null`).
     Never emit driver-only stacks.
3. **Hash:** FNV-1a 64 over `fn|file|line` joined by `\n`, as 16 hex characters. The
   `callsite` record is sent once per hash per process (bounded set of 10,000; LRU).

## 8. Runtime model

* **Handlers.** Every subscribed handler body is wrapped in `try/catch(Exception)`. The catch
  increments an internal error counter and never rethrows. After 1,000 internal errors in
  60 s the exporter disables itself (dormant, with a warning written once via `ILogger` if
  configured, else `Trace`).
* **No locks across I/O.** Handlers write fixed-size structs into a bounded single-consumer
  ring (default 65,536 entries). When full: drop and count.
* **Sender.** One background thread drains the ring and serializes NDJSON with
  `Utf8JsonWriter` into pooled buffers. It POSTs via a dedicated `HttpClient` with
  `SocketsHttpHandler`:
  * `PooledConnectionLifetime=2 min`;
  * `ConnectTimeout=2 s`;
  * request timeout 5 s.

  It follows 02 §5 for status handling, and emits `stats` every 10 s.
* **Dormant mode.** Handlers do one volatile read and return. Activity listening is not
  registered. `SPYGLASS_DISABLED=1` additionally skips every subscription.
* **Shutdown.** On `AppDomain.ProcessExit`, flush for at most 1 s.

## 9. Budget (enforced by 06 §4)

Measured per command, with BenchmarkDotNet paired runs (plain vs instrumented) against the
reference fake, steady state:

| Mode | Added CPU | Added managed allocation |
|---|---|---|
| Dormant | ≤ 0.2 µs | 0 B |
| Active, no callsite capture | ≤ 3 µs | ≤ 64 B (amortized; the ring entry is a struct, serialization uses pooled buffers) |
| Active, sampled stack capture on the sampled command | ≤ 60 µs on that command only | — |

These exclude the driver's own cost of raising command events, which the driver pays only
when a subscriber exists. That cost is measured and reported separately: "driver event
overhead".

## 10. Tests

* **Unit:**
  * join key normalization against the 02 §3.2 vectors;
  * `LibraryInfo` composition;
  * pool-wait pairing;
  * UoW resolution with nested activities;
  * callsite filtering;
  * dormant-mode zero allocation (`GC.GetAllocatedBytesForCurrentThread`).
* **Integration** (06 §2): ASP.NET Core sample → proxy → reference fake and a real mongod.
  Assert that:
  * join rate ≥ 99.9%;
  * `uow.name == "GET /orders/{id}"`;
  * an injected 50 ms pool contention shows up as `poolWait_us ≥ 50,000` on the affected ops;
  * a cursor left undisposed raises `cursor-not-exhausted`.
* **Failure:** collector returns 500 / 429 / hangs; ingest URL unreachable; malformed
  `$spyglass`. The application's throughput stays within budget, and no exception reaches
  the application.
