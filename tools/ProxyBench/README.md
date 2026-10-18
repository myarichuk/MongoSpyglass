# ProxyBench

A/B harness for the proxy performance budget (docs/spec/01-proxy.md §14, 06-testing.md §4):
the MongoDB .NET driver → (direct | proxy) → a real `mongod`, each in its own process.

```sh
tools/mongod/fetch.sh                                   # mongod without Docker
dotnet build -c Release src/Spyglass.Proxy tools/ProxyBench
dotnet tools/ProxyBench/bin/Release/net10.0/ProxyBench.dll \
  --mongod tools/mongod/.cache/8.0.23/bin \
  --proxy src/Spyglass.Proxy/bin/Release/net10.0/spyglass-proxy.dll \
  --rounds 3 [--modes direct,bare,observe,export] [--json out.json] \
  [--proxyEnv "K=V;K2=V2"]
```

| Mode | Proxy configuration |
|---|---|
| `direct` | no proxy |
| `bare` | `limits.observeQueueBytes=0`: forwarding only, no tap |
| `observe` | defaults, headless (metrics, no export) |
| `export` | exporting every record to an in-process sink answering 204 |

Measured per mode: sequential point-lookup latency (p50/p99/p999), throughput with 32
concurrent clients, time to the first 1024 × 4 KB batch (~4 MiB reply), and — from the
proxy's own `/metrics` over the throughput window — allocated bytes per forwarded frame and
proxy CPU µs per operation.

**Read the throughput ratio with the machine in mind.** When client, mongod and proxy share
few cores and the client saturates them, every µs of proxy CPU is taken from the other two,
so the ratio measures CPU cost more than forwarding overhead. Run the proxy on its own cores
(`taskset`) for a ratio that reflects deployment.

The zero-allocation gate for the forwarding path lives in the test suite
(`AllocationTests`, Release only): a raw-socket client that allocates nothing, so every byte
the process allocates is the proxy's.
