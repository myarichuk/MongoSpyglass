# MongoSpyglass

A MongoDB profiler you deploy by changing a connection string. A byte-transparent wire proxy sits in front of
your MongoDB deployment, forwards everything unchanged, and ships what it observed to a collector that groups
operations by shape, keeps latency distributions, tracks cursors, raises alerts and serves a UI. No driver
plugin, no agent in the application, no database credentials in the profiler.

```
services ──(MongoDB wire protocol)──▶ spyglass-proxy ──▶ MongoDB (standalone, replica set, sharded)
                                           │ NDJSON records over HTTP
                                           ▼
                                    spyglass-collector  ── UI, REST + SSE API, Prometheus /metrics
```

**Status:** M1 (proxy + wire-only collector) and M2 (ingest protocol frozen, collector join) are built and
tested; see [docs/spec/STATUS-M1-proxy.md](docs/spec/STATUS-M1-proxy.md),
[STATUS-M1-collector.md](docs/spec/STATUS-M1-collector.md) and [STATUS-M2.md](docs/spec/STATUS-M2.md). The
driver exporters that add application context (unit of work, pool wait, callsites) are specified (M3, M4) and
not yet written. What comes next is in [docs/PLAN.md](docs/PLAN.md). Not designed for production traffic.

## What you get

Per operation: command, namespace, server latency (first reply byte minus last request byte, measured at the
proxy), request and reply sizes, document counts, errors, the redacted command, and which service sent it.
Across operations: shapes (the command with values erased) with p50/p95/p99 per minute, the slowest and latest
examples of each, cursor lifecycles (exhausted, killed, expired, abandoned), and alerts for slow shapes, p95
regressions, suspected N+1 loops, repeated identical queries, unbounded scans, oversized results, large `$in`
lists, abandoned cursors, error bursts and unacknowledged writes. Headless: the proxy alone exposes per-command
Prometheus histograms labelled by command, database, collection, app and status.

## Try it

```sh
SPYGLASS_TOKEN=some-secret SPYGLASS_ADMIN_PASSWORD=some-password docker compose up --build
```

Then open http://localhost:8080 (the password you set), click around http://localhost:5000 (a demo service
whose connection string points at the proxy) and watch its operations arrive. Your own tools can go through the
proxy too: `mongosh "mongodb://localhost:27018/?appName=me"`. The compose file explains the pieces.

Set `appName` in every connection string that goes through the proxy. It is how the UI tells services apart; a
driver without it shows up under the driver's name.

## Deploy it

One proxy per upstream MongoDB deployment, one collector per environment.

**Proxy** (`src/Spyglass.Proxy`, BCL only, no runtime dependencies). Configuration is a YAML or JSON file
(`--config`), environment variables (`SPYGLASS_` prefix, `__` between sections) or flags, in that order of
precedence; every key is in [docs/spec/01-proxy.md §2](docs/spec/01-proxy.md). The essentials:

```sh
spyglass-proxy --upstream.uri "mongodb://db1:27017,db2:27017,db3:27017/?replicaSet=rs0" \
  --listen.bindAddress 0.0.0.0 --listen.advertiseHost spyglass-proxy.staging.internal --listen.basePort 27017 \
  --collector.endpoint http://spyglass-collector:8080/ingest/v0 --collector.token "$SPYGLASS_TOKEN"
```

* A replica set gets one listener per member (`basePort`, `basePort+1`, …) and the proxy rewrites the hosts in
  hello replies to those listeners, so drivers discover the set only through the proxy. `advertiseHost` must be
  the name clients reach the proxy by. Members added later get listeners from `listen.portRange`.
* TLS to the upstream follows the URI (`tls=true`, `mongodb+srv://`); TLS to clients is `listen.tls.mode=tls`
  with your certificate, or a self-signed one whose fingerprint is logged.
* `capture.values` decides what leaves the proxy: `full`, `truncated` (default: strings cut at 64 characters)
  or `shape` (types only, no values). Authentication payloads and fields matching
  `capture.redactFieldPattern` are redacted in the proxy before anything is queued.
* `/metrics`, `/healthz` and `/readyz` are on `metrics.bind` (default `127.0.0.1:9187`).
* Container: `docker build -f src/Spyglass.Proxy/Dockerfile -t spyglass-proxy .`

**Collector** (`src/Spyglass.Collector` + `src/Spyglass.Collector.Ui`). SQLite in `SPYGLASS_DATA`, 24 h of
operations by default, aggregates kept 30 days. `SPYGLASS_TOKEN` authenticates ingest; the UI uses
`SPYGLASS_ADMIN_PASSWORD`, or `SPYGLASS_AUTH_HEADER=X-Forwarded-User` behind an authenticating reverse proxy.
It speaks plain HTTP: terminate TLS in front of it.

```sh
docker build -f src/Spyglass.Collector/Dockerfile -t spyglass-collector .
docker run -d -p 8080:8080 -v spyglass-data:/data -e SPYGLASS_TOKEN=… -e SPYGLASS_ADMIN_PASSWORD=… spyglass-collector
```

All settings: [docs/spec/03-collector.md §9](docs/spec/03-collector.md) and `src/Spyglass.Collector/CollectorOptions.cs`.

## Build and test

.NET 10 SDK. The proxy suite runs real `mongod`/`mongos` processes (fetched once, no Docker) and, for the
Node driver conformance tests, Node.js.

```sh
dotnet build MongoSpyglass.sln
dotnet test tests/Spyglass.Collector.Tests                       # no external processes
tools/mongod/fetch.sh && (cd tests/node-conformance && npm install)
dotnet test tests/Spyglass.Proxy.Tests                           # real mongod: standalone, TLS replica set, sharded
dotnet test -c Release tests/Spyglass.Proxy.Tests tests/Spyglass.Collector.Tests   # + allocation and CPU gates
tools/collector/publish.sh                                       # collector + UI into artifacts/collector
```

Performance A/B against a real mongod: [tools/ProxyBench](tools/ProxyBench/README.md).

## Repository

| Path | What |
|---|---|
| `src/Spyglass.Proxy` | the proxy: forwarding, topology, TLS, observation, redaction, metrics, export |
| `src/Spyglass.Collector` | ingest, join, shapes, analyzers, SQLite storage, API |
| `src/Spyglass.Collector.Ui` | Blazor WebAssembly SPA served by the collector |
| `tests/` | proxy conformance (real mongod, .NET and Node drivers), collector unit, vector and bench tests |
| `tools/` | `ProxyBench` (perf A/B), `mongod/fetch.sh`, `collector/publish.sh` |
| `docs/spec/` | the specification, conformance vectors and status per milestone |
| `docs/PLAN.md` | what is next, in order, with the reasoning |
| `DemoApp/` | a small ASP.NET service used by `docker-compose.yml` to generate traffic |

The specification is the source of truth for behaviour; code comments cite it by section (`01 §5`, `03 §4`).
The October 2026 review that led to this architecture is in `docs/review/`.

## License

See [LICENSE.txt](LICENSE.txt).
