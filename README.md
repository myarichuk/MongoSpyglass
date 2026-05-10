# MongoSpyglass (Work In Progress)

**Note: This project is currently a Work In Progress (WIP).**

MongoSpyglass is a low-level proxy service designed to be an extremely efficient, zero-allocation profiler for MongoDB queries. It aims to provide functionality and user stories similar to Hibernating Rhinos ORM profilers, featuring a cross-platform GUI.

## Architecture & Design

- **Target Framework:** .NET 10
- **Zero-Allocation Data Path:** The proxy uses `SharpArena` (`ArenaAllocator`), `System.Buffers.ArrayPool`, `Span<T>`, `ref struct`, and `unsafe` contexts for high-performance memory management alongside asynchronous I/O.
- **String Hashing:** To avoid string allocations, payload parsing prefers calculating string hashes (like FNV-1a) directly from `ReadOnlySpan<byte>` buffers instead of materializing managed strings.
- **Frontend UI:** Built as a Blazor Web project utilizing Tailwind CSS for styling and prioritizing Radzen components (reskinned as needed) over custom components.

## Current wire-protocol support

The proxy currently validates/parses these MongoDB operations and then forwards the original bytes unmodified:

- `OP_MSG` (MongoDB 3.6+, including modern handshake via `hello` / `isMaster` command payload).
- `OP_QUERY` (legacy handshake/query path used by older drivers/mongos).

All other opcodes are forwarded transparently without mutation. This keeps backwards compatibility while avoiding frame corruption for unimplemented messages.

## Development & Testing

The project uses standard .NET CLI commands.

### Build
```bash
dotnet build MongoSpyglass.sln
```

### Test
```bash
dotnet test MongoSpyglass.sln
```

For zero-allocation testing on the hot path, tests use `GC.GetAllocatedBytesForCurrentThread()` to assert 0 managed allocations. Network payload streams are simulated via `MemoryStream` and `PartialReadStream`.

## Docker Compose Environment

A full development environment can be launched using Docker Compose, which spins up:
- MongoDB database (port 27017)
- MongoSpyglass proxy (listening on 27018)
- DemoApp web application (connected through the proxy)

```bash
docker-compose up -d
```

## Protocol reference

- Detailed annotated protocol notes (including legacy paths): `docs/mongodb-wire-protocol.md`.
