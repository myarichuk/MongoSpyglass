# MongoSpyglass v1 — specification

Status: **Draft 1** · 2026-10-17 · supersedes the architecture sketch in
`docs/review/2026-10-17-architecture-review.md` §9.

The keywords MUST, MUST NOT, SHOULD, SHOULD NOT and MAY are used as in RFC 2119.

| Doc | Scope | Milestone |
|---|---|---|
| [01-proxy.md](01-proxy.md) | Wire proxy: forwarding, topology, TLS, capture, redaction | M1 |
| [02-ingest-protocol.md](02-ingest-protocol.md) | Spyglass Ingest Protocol v0: discovery, join key, records, transport | M2 (wire part in M1) |
| [03-collector.md](03-collector.md) | Collector: ingest, join, shapes, analyzers, storage, API, UI | M1 (wire-only) → M2 (join) |
| [04-exporter-dotnet.md](04-exporter-dotnet.md) | Optional .NET driver exporter | M3 |
| [05-exporter-node.md](05-exporter-node.md) | Optional Node.js driver exporter | M4 |
| [06-testing.md](06-testing.md) | Conformance vectors, CI matrix, performance gates, fake-server requirements | all |

---

## 1. Product

**"I have a system in staging and I can't debug real issues locally."**

You point one or more real services at Spyglass instead of at MongoDB, by changing the
connection string. Spyglass forwards every byte to the real deployment and shows, per
operation:

* what ran, against what, and how long the server took;
* what came back (size, document count, errors);
* which operations share a shape;
* what looks wrong: slow shapes, N+1 patterns, repeated identical queries, leaked cursors,
  full-collection scans, oversized results, error bursts.

No code change, no agent, and no dependency in the application.

Optionally, a service can add an **exporter** package for its driver. The exporter supplies
context that cannot be seen on the wire:

* which HTTP request (unit of work) issued the operation;
* how long it waited for a pooled connection;
* the client-observed duration;
* the source location.

Exporters never become required: every feature that can be computed from the wire is
computed from the wire.

### 1.1 Personas

* **Service developer.** Their service misbehaves in staging and they need "what did my
  request actually send to Mongo, and why was it slow".
* **Platform / SRE.** Wants a per-service, per-shape picture of database load without
  touching application code. Will run it headless and scrape the metrics endpoint.

### 1.2 Non-goals for v1

* Production use under load (supported, but not designed or tested for it).
* `loadBalanced=true` deployments (Atlas serverless / LB mode).
* User authentication with `MONGODB-X509` (client certificates can't be forwarded through a
  terminating proxy) and `GSSAPI`/Kerberos (service principal is bound to the server
  hostname).
* Observing payloads of client-side field-level encryption / Queryable Encryption. Those
  values are opaque BinData and are forwarded unchanged.
* Exporter-only mode (an exporter without a proxy). Exporters send context, not content
  (§3, I6); without a proxy there is nothing for that context to attach to.
* Running `explain` or any other command against the user's database. The collector holds
  no database credentials.

---

## 2. Components

```
           ┌──────────── staging services (unchanged; connection string points at the proxy) ────────────┐
           │  svc-a (.NET, +exporter)        svc-b (Node, +exporter)          svc-c (Java, nothing)     │
           └──────┬──────────────────────────────┬─────────────────────────────────┬────────────────────┘
                  │ MongoDB wire protocol (plain or TLS)                           │
                  ▼                              ▼                                 ▼
           ┌────────────────────────── spyglass-proxy (one per upstream deployment) ─────────────────────┐
           │  listeners: one per replica-set member / mongos      byte-transparent forwarding (§3, I2) │
           │  observation tap → bounded capture → redaction → wire records                             │
           └──────┬──────────────────────────────────────────────────────────────────────────┬─────────┘
                  │ MongoDB wire protocol (TLS as configured)                                │ HTTP POST /ingest/v0
                  ▼                                                                          ▼
           ┌───────────────────────┐                                           ┌───────────────────────────┐
           │ real MongoDB (RS /     │       exporters ── HTTP POST /ingest/v0 ─▶│ spyglass-collector        │
           │ sharded / standalone)  │       (context records, optional)         │ join · shapes · analyzers │
           └───────────────────────┘                                           │ storage · API · UI        │
                                                                               └───────────────────────────┘
```

| Component | Deployable | Dependencies |
|---|---|---|
| `spyglass-proxy` | single self-contained binary / container | .NET BCL only (no NuGet packages at runtime) |
| `spyglass-collector` | single container (API + UI + storage) | allowed (see 03 §7) |
| `Spyglass.MongoDb.Exporter` (.NET) | NuGet package | `MongoDB.Driver` only |
| `@spyglass/mongodb-exporter` (Node) | npm package | none at runtime; `mongodb` as peer dependency |

---

## 3. Invariants

Every component spec refers back to these by number. A change that violates one is a design
change, not an implementation detail.

* **I1 — Forwarding never waits on observation.** No observation work (parsing, redaction,
  hashing, exporting, storage) runs on, or blocks, the forwarding path. The only exceptions
  are the enumerated handshake rewrites (I2), which are bounded in size and CPU.
* **I2 — Byte transparency.** The proxy forwards every frame byte-for-byte, except for these
  rewrites, which are exhaustive:
  * **R1:** topology fields in hello-family replies (`hosts`, `passives`, `arbiters`,
    `primary`, `me`);
  * **R2:** insertion of the `$spyglass` discovery document into hello-family replies;
  * **R3:** removal of `compression` from client hello requests, when `compression=strip`.

  A rewritten message is re-framed with a correct `messageLength`, its checksum flag
  cleared, and its checksum removed.
* **I3 — 1:1 connections.** Each client connection maps to exactly one upstream connection
  for its whole lifetime. The proxy never pools, multiplexes or reorders. The join key (02 §3)
  depends on this.
* **I4 — Observation is lossy; forwarding is not.** Every observation buffer is bounded in
  bytes. When a bound is hit, observation data is dropped and counted. Forwarding is never
  slowed to make room.
* **I5 — Exporters are optional and fail-safe.**
  * A missing, misconfigured, slow or crashing exporter MUST NOT change application
    behaviour beyond the documented CPU and allocation budget.
  * A missing exporter MUST NOT disable any wire-derived feature.
* **I6 — Exporters send context, never content.** Exporters never send command bodies,
  filter values or reply documents; the proxy already has them, and redacts them in one
  place. Exporters send identifiers, timings, unit-of-work names and source locations.
* **I7 — No credential leaves the proxy.** Authentication payloads and secret-bearing fields
  are redacted inside the proxy, before anything is queued for export (01 §9).

---

## 4. Milestones and exit criteria

| | Deliverable | Exit criteria |
|---|---|---|
| **M1** | Proxy v1 + collector v1 (wire-only) | All of 06 §2 (proxy conformance) green against a real 3-member TLS replica set, using both the .NET and Node drivers. Proxy performance gate (06 §4) green. Redaction suite (06 §2.6) green. UI shows the live feed, shapes, operation detail and alerts. Headless `/metrics` works. |
| **M2** | Ingest Protocol v0 frozen + collector join | Conformance vectors (06 §3) published. Collector joins synthetic exporter records at ≥ 99.9% on the harness. Late, duplicate and orphan records are handled per 02 §6. |
| **M3** | .NET exporter | Join rate ≥ 99.9% end-to-end. Exporter budget (04 §9) green. Pool-wait, unit-of-work grouping and callsites visible for an ASP.NET Core sample. Dormant cost is measured and documented. |
| **M4** | Node.js exporter | Same as M3, for an Express sample and a plain `node:http` sample. |

---

## 5. Glossary

| Term | Meaning |
|---|---|
| **member** | One upstream `mongod` or `mongos` process the proxy forwards to. |
| **listener** | A proxy TCP endpoint that forwards to exactly one member. |
| **advertised address** | `host:port` of a listener as clients see it. It's written into rewritten hello replies. |
| **serverConnectionId** | The `connectionId` field of the hello reply on an upstream connection. Unique per member process lifetime. |
| **operation (op)** | One request frame and its reply frame(s). For `moreToCome` requests, the request alone. |
| **unit of work (UoW)** | An application-level scope (usually one inbound HTTP request) that issues zero or more ops. Known only through an exporter. |
| **shape / shapeHash** | The normalized structure of a command with values erased (03 §4). |
| **valueHash** | The normalized command *including* values. Equal valueHash means an identical query. |
| **wire record / context record** | A record produced by the proxy / by an exporter. |

---

## 6. What carries over from the current code

| Keep | Rewrite | Delete |
|---|---|---|
| Frame-length validation idea; OP_MSG section walking; `BsonUtils` display parsing; the harness in `tools/ReviewHarness`; UI information architecture and Tailwind theme | `MongoDbProxy` (becomes cut-through, 01 §3); BSON scanning (becomes a streaming scanner, 01 §10) | NRules engine and all rules; `TrafficMonitorService`; per-operation RavenDB documents and attachments; Autofac, Polly, Stateless and Roslyn references; `WeatherForecast*` |
