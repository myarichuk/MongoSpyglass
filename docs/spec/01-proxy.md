# 01 — spyglass-proxy v1

Invariants I1–I7 are defined in [README §3](README.md#3-invariants).

## 1. Responsibilities

1. Accept MongoDB wire-protocol connections from clients, on one listener per upstream
   member.
2. Forward every byte to the corresponding member and back. Cut-through, with no
   whole-frame buffering (except the R1–R3 handshake rewrites).
3. Make topology discovery work through the proxy: replica sets, sharded clusters,
   standalones.
4. Terminate and originate TLS as configured.
5. Observe each frame through a bounded tap, extract wire facts, redact, and export wire
   records to the collector (02).
6. Expose self-telemetry and per-command Prometheus metrics (§12), so headless mode is
   useful without a collector.

The proxy MUST have no runtime NuGet dependencies. Everything (TLS, JSON, HTTP client, DNS
SRV resolution, Prometheus text format, CRC) is implemented with the BCL or in-tree.

## 2. Configuration

Configuration comes from one YAML/JSON file (`--config`), overridden by environment
variables (`SPYGLASS_` prefix, `__` as the section separator), overridden by CLI flags.
Configuration is static: any change requires a restart, and the proxy MUST NOT drop live
connections because of configuration reload (there is none).

```yaml
upstream:
  uri: "mongodb+srv://cluster0.example.net/?replicaSet=rs0"   # or mongodb://h1:27017,h2:27017/?replicaSet=rs0
  topology: auto            # auto | standalone | replicaSet | sharded
  tls:
    enabled: auto           # auto = on if uri says tls=true or scheme is +srv
    caFile: null            # PEM bundle; default = system trust store
    allowInvalidCertificates: false
    allowInvalidHostnames: false

listen:
  bindAddress: 127.0.0.1    # 0.0.0.0 MUST be explicit
  advertiseHost: localhost  # host written into rewritten hello replies
  basePort: 27017           # member i gets basePort + i, in discovery order
  portRange: [27017, 27099] # dynamically discovered members are allocated here
  tls:
    mode: none              # none | tls
    certFile: null          # PEM; if mode=tls and null → generate self-signed at start, log SHA-256 fingerprint
    keyFile: null

compression: strip          # strip | opaque | observe   (§8)

capture:
  commandBytes: 16384       # max bytes of each request body copied for observation
  replyBytes: 0             # max bytes of each reply body copied (0 = facts only, no reply content)
  values: truncated         # full | truncated | shape   (§9.3)
  truncateStringsAt: 64
  redactFieldPattern: "(?i)^(pass(word)?|pwd|secret|token|api[-_]?key|authorization|cookie|ssn|card(number)?)$"

limits:
  maxClientConnections: 4096
  observeQueueBytes: 67108864   # 64 MiB across all connections (I4)
  maxPendingPerConnection: 1024 # outstanding requests tracked per connection
  helloRewriteMaxBytes: 1048576

collector:
  endpoint: "http://spyglass-collector:4318/ingest/v0"   # null = headless
  token: "${SPYGLASS_TOKEN}"
  batchMaxRecords: 2000
  batchMaxBytes: 1048576
  flushIntervalMs: 500
  bufferBytes: 16777216

discovery:
  advertiseIngest: "http://spyglass-collector:4318/ingest/v0"  # what exporters are told; default = collector.endpoint
  advertiseToken: true      # include the ingest token in $spyglass (see 02 §2.3)

metrics:
  bind: 127.0.0.1:9187      # Prometheus text endpoint + /healthz + /readyz
  commandLabels: [command, database, collection, app]
  maxSeries: 5000           # beyond this, new label sets are folded into collection="__other__"
```

Validation MUST happen at startup. Invalid configuration exits non-zero with a message
naming the key. In particular:

* `bindAddress` other than loopback, combined with `listen.tls.mode=none`, logs a warning:
  traffic would be plaintext on the network.
* `upstream.tls.enabled=false` against a `+srv` URI is an error.

## 3. Forwarding path

### 3.1 Connection lifecycle

1. Accept on listener L, which is bound to member M (§5).
2. Set `NoDelay=true` and `KeepAlive=true` on the accepted socket.
3. Connect to M. Set `NoDelay=true` and `KeepAlive=true`, and run the TLS handshake if
   configured (§6). If upstream connect or TLS fails, close the client socket immediately.
   Do not accept bytes that cannot be delivered. Emit `conn` with
   `closeReason=upstream_connect_failed`.
4. Start two pumps: client→server and server→client. Each pump owns:
   * its socket read buffer (default 64 KiB, pooled);
   * its frame tracker (§3.2);
   * its tap (§4).
5. When either side closes (FIN or RST), propagate a shutdown of the corresponding direction
   to the other side. Then close both sockets when both pumps finish, or 30 s after the
   first one finishes.
6. On close, emit a `conn` close record with byte counters and the reason.

### 3.2 Frame tracking (cut-through)

The pump forwards bytes **as they arrive**. It writes every read chunk to the peer before
reading the next chunk (one write per read, with no per-frame flush). In parallel it runs a
frame tracker:

```
state: HEADER(need=16, buf[16]) | BODY(remaining)
on chunk:
  while chunk not empty:
    HEADER: copy min(need, len) into buf; if complete:
              len = i32le(buf[0..4])
              if len < 16 or len > maxFrame → FAIL_CLOSED
              begin frame (requestId, responseTo, opCode from buf) → tap.begin(...)
              remaining = len - 16 → BODY
    BODY:   n = min(remaining, len(chunk)); tap.body(chunk[..n]); remaining -= n
            if remaining == 0 → tap.end(); HEADER
```

* `maxFrame` starts at 48,000,000. It is replaced by `maxMessageSizeBytes` from the first
  hello reply on the connection.
* **FAIL_CLOSED:** close both sockets and emit `conn` with `closeReason=invalid_frame` and
  the offending 16 header bytes (hex). The proxy MUST NOT keep buffering or forwarding a
  stream it can no longer frame.
* `tap.*` calls are O(1) apart from copying at most the configured capture bytes. They MUST
  NOT allocate on the steady-state path.

Timestamps are taken with a monotonic clock (`Stopwatch.GetTimestamp`) at:

* `tFirst`: the read that completed the header;
* `tLast`: the read that completed the body.

### 3.3 Handshake inspection and rewrites (bounded exception to I1)

The client→server pump inspects the first ≤ 256 bytes of every request frame to get its
command name:

* **OP_MSG:** the first element name of the kind-0 section.
* **OP_QUERY:** the target must be `*.$cmd` (the first element name of the query document),
  else the frame is a legacy query.
* **OP_COMPRESSED:** use the inner `originalOpcode`. The command name is *unknown* unless
  `compression=observe`.

That's constant work per frame. The name is recorded in the connection's pending table
(§7), keyed by `requestId`.

**Hello family** = `hello`, `isMaster`, `ismaster`, legacy OP_QUERY `isMaster` on
`admin.$cmd`.

* **R3 (client hello, `compression=strip`).** For a hello-family request carrying a
  `compression` array, buffer the frame (≤ `helloRewriteMaxBytes`), remove the element,
  re-frame, and forward. Hello requests are never compressed (wire spec), so this is always
  possible.
* **R1 + R2 (hello reply).** When a reply's `responseTo` matches a pending hello-family
  request, buffer that reply frame:
  1. Apply R1 (§5.3). Apply R2: append `$spyglass` (02 §2.2) as the **last** top-level
     element.
  2. Re-frame. If the OP_MSG `checksumPresent` flag was set, clear it and drop the 4
     checksum bytes.
  3. Forward.

  If the reply has the OP_MSG `moreToCome` flag (streaming hello), register its `requestId`
  as the next expected `responseTo` for that stream (§7). Subsequent stream replies are
  rewritten the same way.
* If any rewrite fails (malformed BSON, over the size cap), forward the original bytes
  unmodified and increment `spyglass_proxy_rewrite_skipped_total{reason}`. Never fail the
  connection because of a rewrite.

From every hello reply, also record into connection state (not the forwarding path):

* `connectionId` → `serverConnectionId`;
* `maxMessageSizeBytes`, `maxWireVersion`, `topologyVersion`;
* `setName`, `msg` (`isdbgrid` ⇒ mongos);
* `compression` (what the server agreed to).

From the first client hello, record `client` metadata: `application.name`, `driver.name`,
`driver.version`, `os.type`, `platform`, `env`. Parse `driver.name` on `|` to detect an
exporter marker (02 §2.4).

## 4. Observation tap

Each pump has a per-connection capture ring of fixed-size slots (default 32 slots). A slot
holds:

```
struct FrameCapture {
  long   connSeq;            // proxy-local connection sequence number
  byte   direction;          // 0 = request, 1 = reply
  int    requestId, responseTo, opCode, originalOpCode, flags;
  int    frameLength;
  long   tFirst, tLast;      // monotonic ticks
  int    capturedLength;     // ≤ capture.commandBytes / replyBytes
  bool   truncated;
  ReplyFacts facts;          // replies only, filled by the streaming scanner (§10)
  byte[] buffer;             // pooled, length = capture limit
}
```

* `tap.begin` claims a slot. If none is free, it marks the frame `unobserved` and counts a
  drop (I4). Forwarding continues regardless.
* `tap.body` copies bytes into the slot only while `capturedLength < limit`. For replies, it
  also feeds the streaming scanner, which inspects and does not copy.
* `tap.end` publishes the slot to the **observer queue**. That's an MPSC queue bounded by
  `limits.observeQueueBytes`, counting `capturedLength`. If enqueueing would exceed the
  bound, the slot is released and a drop is counted.
* Observer workers (default `max(1, cores/2)`) consume the queue with **per-connection
  affinity** (`connSeq mod workers`), so per-connection ordering is preserved for
  correlation (§7).
* Publishing never takes a lock, and a pump almost never wakes a worker. A wake is a futex call
  on the pump thread, and below saturation the worker is asleep at nearly every frame. So a
  worker sleeps on a short timer (1 ms after a drain that found work, backing off to 10 ms
  when idle), and a pump wakes it only on every 64th publish, which bounds the queue in a
  burst. Observation therefore runs up to ~10 ms behind the wire. Nothing downstream can
  tell: records are batched for `collector.flushIntervalMs`, and metrics are scraped.
* Observer state that repeats across operations is cached per worker: interned
  command/db/collection names, lsid hashes by session id (drivers pool sessions, and SHA-256
  is microseconds a call), and field-pattern verdicts by name. Each cache is bounded and stops
  admitting new entries when full, so client-chosen names cannot grow it.

The tap never allocates per frame in steady state. Buffers come from a pool sized at startup
and grown only up to the queue byte bound.

## 5. Topology

### 5.1 Member discovery at startup

1. Resolve `upstream.uri`. For `mongodb+srv`, query `_mongodb._tcp.<host>` SRV and `<host>`
   TXT with an in-tree RFC 1035 UDP resolver, using `/etc/resolv.conf` nameservers with TCP
   fallback on truncation. Apply TXT options `replicaSet` and `authSource` per the
   initial-DNS-seedlist spec.
2. Open a **monitor connection** to each seed (TLS as configured) and send
   `hello` with `{ hello: 1, helloOk: true, client: { driver: { name: "spyglass-proxy", version } } }`.
   hello requires no authentication.
3. Determine the topology:
   * `msg: "isdbgrid"` ⇒ **sharded**: every seed is a mongos member. No R1 rewrites.
   * `setName` present ⇒ **replicaSet**: the members are the union of `hosts`, `passives`,
     `arbiters` (and `primary`).
   * otherwise ⇒ **standalone**: one member.

   `upstream.topology` other than `auto` asserts the result. A mismatch is a startup error.
4. Allocate one listener per member: `basePort + i` in the order members are first seen.
   That's deterministic for a fixed seed list, so client connection strings stay stable
   across restarts.
5. Keep the monitor connections running hello every 10 s. Members that appear later are
   allocated a listener from `portRange` on first sight, whether seen by the monitor or in
   a forwarded hello reply. Members that disappear keep their listener until no client
   connections remain *and* they've been absent for 10 minutes.

Monitor connections are proxy-internal. They are never reported as client ops.

### 5.2 Client connection strings

| Upstream | What the service uses |
|---|---|
| standalone | `mongodb://<advertiseHost>:<basePort>/` |
| replica set | `mongodb://<advertiseHost>:<basePort>/?replicaSet=<setName>` (the driver discovers the other members via rewritten `hosts`). `directConnection=true` also works and pins to one member. |
| sharded | `mongodb://<advertiseHost>:<basePort>,<advertiseHost>:<basePort+1>,…/` (one per mongos) |

Services MUST NOT use `mongodb+srv://` to reach the proxy: SRV would resolve to the real
hosts. Credentials, `authSource`, `readPreference` etc. stay in the service's string
unchanged. `tls` in the service's string is about the client↔proxy hop (§6.2).

### 5.3 R1: topology rewriting (replica set only)

In every hello-family reply forwarded on a listener for member M:

* Each string in `hosts`, `passives` and `arbiters`, and the strings `primary` and `me`, is
  parsed as `host:port`, normalized (02 §3.2), and replaced by the advertised address of
  that member's listener. Unknown members are allocated a listener first.
* `me` MUST map to the advertised address of the listener the reply is forwarded on. If it
  doesn't (the member reports a different `me` than the one this listener was allocated
  for), log an error. Then replace it with this listener's address anyway: drivers drop
  members whose `me` doesn't match the address they dialled.
* `setName`, `setVersion`, `electionId`, `topologyVersion`, tags and everything else are
  left unchanged.

Sharded and standalone hello replies get R2 only.

## 6. TLS

### 6.1 Upstream (proxy → member)

* `SslStream` with SNI = the member's hostname.
* Validation uses `upstream.tls.caFile` (or the system store) and hostname checking against
  the member's DNS name. The `allowInvalid*` flags are honoured and logged at warning on
  every start.
* TLS 1.2+.
* No client certificate in v1 (X.509 user auth is a non-goal).

### 6.2 Downstream (client → proxy)

* `mode: none` (default): plaintext. Services set `tls=false` (or omit it) in their proxy
  connection string.
* `mode: tls`: `SslStream` server with the configured certificate, or a self-signed one
  generated at startup for `advertiseHost`. Its SHA-256 fingerprint is logged and exposed at
  `/readyz`. Services use `tlsCAFile` or (staging only) `tlsAllowInvalidCertificates=true`.

### 6.3 Authentication pass-through

| Mechanism | v1 | Notes |
|---|---|---|
| SCRAM-SHA-1 / SCRAM-SHA-256 (including `speculativeAuthenticate` in hello) | **supported** | MongoDB SCRAM has no channel binding, so it passes through a terminating proxy unchanged. |
| PLAIN (LDAP) | supported | The password is plaintext in `saslStart`; redaction (§9) is mandatory. |
| MONGODB-OIDC | supported | Bearer token in `saslStart`; redacted. |
| MONGODB-AWS | expected to work, **to be verified in CI** | Signs an STS request, not the server identity. |
| MONGODB-X509 | not supported | The client certificate terminates at the proxy. |
| GSSAPI | not supported | The service principal is bound to the server hostname. |

## 7. Request/response correlation

The observer keeps a per-connection pending table: `requestId → PendingRequest{commandName,
tFirst, tLast, capture, flags}`, bounded by `maxPendingPerConnection` (oldest evicted and
counted).

| Situation | Rule |
|---|---|
| Request with OP_MSG `moreToCome` (unacknowledged write) | Emit `op` immediately with `status=unacknowledged` and no reply facts. Don't insert into the pending table. |
| Reply with `responseTo` in the table | Complete the op. Server latency = `reply.tFirst − request.tLast`. Emit `op`. |
| Reply with OP_MSG `moreToCome` (exhaust cursor / streaming hello) | Complete the op as above with `stream=true`. Register `reply.requestId → same logical op` so the next reply in the stream correlates. Each further reply emits a `stream` record (02 §4.2), not a new op; the collector counts them into `replyCount`. |
| Reply with unknown `responseTo` | Count `spyglass_proxy_uncorrelated_replies_total`. No record. |
| Connection closes with pending requests | Emit `op` with `status=connection_closed` for each. |

Request timestamps are taken on the request pump *before* forwarding completes (§3.2).
Correlation therefore never races against the server's reply.

## 8. Compression

| Mode | Client hello | On the wire | What the proxy sees |
|---|---|---|---|
| `strip` (default) | R3 removes `compression` | uncompressed on both hops | everything |
| `opaque` | unchanged | whatever was negotiated | OP_COMPRESSED frames get header facts and sizes only; the command name comes from the reply if possible, else `"(compressed)"` |
| `observe` | unchanged | whatever was negotiated | the observer (never the pump) decompresses captured bytes. v1 supports `zlib` (BCL `ZLibStream`) only. `snappy`/`zstd` frames are treated as `opaque` and counted. |

`strip` costs bandwidth on the proxy→member hop. It's the default because it keeps the
proxy dependency-free and fully observant.

## 9. Capture and redaction (I7)

Redaction runs on the observer, before a record is queued for export. Raw captured bytes
never leave the proxy process.

### 9.1 Redacted commands

The body is replaced entirely by `{ "$redacted": "<commandName>" }`:

`authenticate`, `saslStart`, `saslContinue`, `getnonce`, `createUser`, `updateUser`,
`copydbgetnonce`, `copydbsaslstart`, `copydb`.

Replies to these are reduced to `ok`/`code`/`codeName` only.

### 9.2 Redacted fields

* `speculativeAuthenticate` in any hello-family request or reply: value replaced by
  `"$redacted"`.
* Any field, at any depth, whose name matches `capture.redactFieldPattern`: value replaced
  by `"$redacted"`. Dotted paths (`{"profile.password": …}` in a filter or `$set`) match if any
  segment matches.
* `lsid.id` is replaced by a 16-hex-char SHA-256 prefix of its bytes. That keeps grouping by
  session without exporting the UUID.

### 9.3 Value modes

| Mode | Leaf values in exported command bodies |
|---|---|
| `full` | as captured |
| `truncated` (default) | strings longer than `truncateStringsAt` are cut and suffixed `…(+N)`; BinData shows subtype + length only; arrays with more than 20 elements keep the first 20 plus `{"$more": N}` |
| `shape` | every leaf replaced by its type tag (03 §4). `valueHash` can't be computed, so duplicate-query detection is off. |

### 9.4 Kind-1 document sequences

For `insert`/`update`/`delete`/`bulkWrite` sequences, export the identifier, the document
count, and the **first** document (redacted per the value mode). Never more.

### 9.5 Truncated captures

If the request body exceeds `capture.commandBytes`, the scanner MUST tolerate truncated
BSON. It exports the complete elements it has and sets `truncated=true`. It MUST NOT emit
partial strings as if they were complete.

## 10. Streaming reply scanner (no buffering)

Replies are not copied beyond `capture.replyBytes` (default 0), but these facts are always
extracted, by a resumable state machine fed by `tap.body` chunks:

| Fact | Source |
|---|---|
| `ok`, `code`, `codeName`, `errmsg` (first 256 bytes) | top-level |
| `cursor.id`, `cursor.ns` | `cursor` subdocument |
| `nReturned` | element count of `cursor.firstBatch` / `cursor.nextBatch`, counted by skipping each element by its length prefix |
| `n`, `nModified`, `writeErrorsCount`, `upsertedCount`, `writeConcernError` (present?) | write replies |
| `cursorsKilled` / `cursorsNotFound` (counts plus the first 16 ids) | `killCursors` replies |
| `topologyVersion`, `connectionId` | hello replies (also parsed on the R1 path) |

The scanner descends only into `cursor` and the write-reply arrays. It skips everything
else by length prefix. Complexity is O(number of top-level elements + batch length) pointer
hops, with zero allocation. It MUST handle element and length boundaries split across chunks.
Its state fits in ≤ 128 bytes per frame.

The request side has the same kind of scanner for two things a truncated capture would lose:
the document count of every kind-1 sequence (§9.4), and the fields drivers put at the *end* of
the command body (`$db`, `lsid.id`, `txnNumber`). Node 6.x, for example, sends
`insert.documents` inline, so a 250-document insert pushes `$db` past a 16 KiB capture.

## 11. Export to the collector

Wire records (02 §4.2) are built by the observer and handed to one sender thread:

* each worker serializes into a reused writer and copies the line into its own pooled 64 KiB
  NDJSON chunk; a batch is a list of chunk segments posted as-is (no per-record arrays, no
  concatenation); a 413 splits a batch by records, at a line boundary;
* in-memory buffer bounded by `collector.bufferBytes`, charged per chunk;
* batches per 02 §5;
* on an unreachable collector or a 5xx: one retry after 1 s, then drop the batch and count
  it;
* on 429: honour `Retry-After` (capped at 10 s), dropping new batches meanwhile.

With `collector.endpoint=null`, records are not built at all (headless mode). The Prometheus
metrics (§12) are still maintained.

## 12. Metrics and health

Prometheus text exposition at `metrics.bind`, implemented in-tree.

Self-telemetry:

```
spyglass_proxy_connections{listener,state="open"}            gauge
spyglass_proxy_connections_total{listener,result}            counter   result = ok|upstream_failed|tls_failed|limit
spyglass_proxy_bytes_total{listener,direction}               counter
spyglass_proxy_frames_total{listener,direction}              counter
spyglass_proxy_invalid_frames_total{listener}                counter
spyglass_proxy_observation_dropped_total{reason}             counter   reason = no_slot|queue_full|pending_evicted|export_dropped
spyglass_proxy_observe_queue_bytes                           gauge
spyglass_proxy_rewrite_total{kind}  / _skipped_total{reason} counter
spyglass_proxy_uncorrelated_replies_total                    counter
spyglass_proxy_observer_busy_microseconds_total{worker}      counter   time a worker spent processing (wake → drained); rate ÷ 1e6 = utilization
spyglass_proxy_forward_added_latency_seconds                 histogram buckets 5e-6 … 5e-3 (proxy residency of a frame's last byte)
```

Per-command metrics (useful without a collector):

```
spyglass_command_duration_seconds{command,database,collection,app,status}  histogram
    buckets: 0.0001,0.00025,0.0005,0.001,0.0025,0.005,0.01,0.025,0.05,0.1,0.25,0.5,1,2.5,5,10
spyglass_command_reply_bytes{command,database,collection,app}              histogram  (powers of 4 from 256 B to 16 MiB)
spyglass_command_returned_documents{command,database,collection,app}       histogram  (1,10,100,1k,10k,100k)
spyglass_command_errors_total{command,database,collection,app,code_name}   counter
spyglass_cursors_open{database,collection}                                  gauge     (keyed by member+cursorId, 03 §5.4 rules)
```

`app` = client metadata `application.name`, or `driver.name` if absent. Label sets beyond
`metrics.maxSeries` fold into `collection="__other__"`.

* `/healthz`: process alive.
* `/readyz`: all listeners bound, and at least one monitor hello succeeded in the last 30 s.

## 13. Failure behaviour

| Failure | Behaviour |
|---|---|
| Collector down / slow | Records dropped per §11; forwarding unaffected. |
| Observer saturated | Captures dropped per §4; forwarding unaffected. |
| Member down | New client connections to its listener fail fast at upstream connect. Existing connections close when the upstream does. |
| Invalid frame | Fail closed (§3.2). |
| Rewrite failure | Forward unmodified (§3.3). |
| Proxy OOM risk | Can't happen by construction: all buffers are bounded by config. Total = `observeQueueBytes + bufferBytes + listeners × connections × 2 × 64 KiB` read buffers. Log the computed worst case at startup. |

## 14. Performance budget (enforced by 06 §4)

The proxy enables `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1` unless the environment sets it:
socket continuations run on the epoll threads, with no thread-pool hop per read and write. That
is safe only because nothing on the forwarding path blocks (I1); measured effect on the mongod
bench: added p50 latency 70–104 µs → 16–46 µs, proxy CPU per op −30–45%.

Loopback, 4 vCPU, the Mongo.Fakes reference backend at its fast-path configuration, and a
real `mongod` in CI:

| Metric | Budget |
|---|---|
| Added round-trip latency, p50, ≤ 16 KiB messages | ≤ 50 µs |
| Added round-trip latency, p99 | ≤ 250 µs |
| Throughput with 32 concurrent clients | ≥ 90% of direct |
| 4 MiB reply, time to last byte | ≤ 110% of direct |
| Steady-state managed allocation on the forwarding path | 0 B/frame |
| Observer CPU at 20k ops/s with the collector attached | ≤ 1 core |
