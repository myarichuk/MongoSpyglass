# MongoSpyglass

## Current wire-protocol support (WIP)

The proxy currently validates/parses these MongoDB operations and then forwards the original bytes unmodified:

- `OP_MSG` (MongoDB 3.6+, including modern handshake via `hello` / `isMaster` command payload).
- `OP_QUERY` (legacy handshake/query path used by older drivers/mongos).

All other opcodes are forwarded transparently without mutation. This keeps backwards compatibility while avoiding frame corruption for unimplemented messages.
