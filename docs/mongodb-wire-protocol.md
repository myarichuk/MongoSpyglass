# MongoDB Wire Protocol (Annotated)

This document summarizes MongoDB's wire protocol design with an emphasis on what this proxy parses today and how legacy messages fit in.

## 1) Common message envelope (`MsgHeader`)

Every MongoDB wire message starts with 16 bytes:

1. `messageLength` (`int32`, little-endian): total frame size including header.
2. `requestID` (`int32`): client-generated id for correlating request/response.
3. `responseTo` (`int32`): request id this message responds to (0 for requests).
4. `opCode` (`int32`): operation discriminator.

### Safety notes

- Reject frames where `messageLength < 16` (invalid frame).
- Unknown `opCode` values should usually be forwarded opaquely to preserve compatibility.

## 2) Modern protocol path: `OP_MSG` (`opCode = 2013`)

`OP_MSG` is used by modern drivers (MongoDB 3.6+), including handshake commands like `hello` (and compatibility `isMaster`).

### `OP_MSG` layout

- `flagBits` (`uint32`)
- one or more sections:
  - **kind 0** (`byte = 0`): single BSON document (command body)
  - **kind 1** (`byte = 1`): document sequence with identifier + BSON docs
- optional checksum (`uint32`) if `checksumPresent` bit is set

### Flag bits

- bit 0: `checksumPresent`
- bit 1: `moreToCome`
- bit 16: `exhaustAllowed`

### Handshake in practice (`OP_MSG`)

Typical first command document from client:

```javascript
{ hello: 1, helloOk: true, client: { ... }, compression: [...] }
```

Compatibility form used by some tools:

```javascript
{ isMaster: 1, helloOk: true, client: { ... } }
```

Server response includes topology + limits (`maxWireVersion`, `minWireVersion`, `isWritablePrimary`, etc.).

## 3) Legacy protocol path: `OP_QUERY` (`opCode = 2004`)

Older clients/tools can send command-style requests via `OP_QUERY` against `admin.$cmd`.

### `OP_QUERY` layout

- `flags` (`int32`)
- `fullCollectionName` (cstring, UTF-8, null terminated)
- `numberToSkip` (`int32`)
- `numberToReturn` (`int32`)
- `query` (BSON document, required)
- `returnFieldsSelector` (BSON document, optional)

### Handshake in practice (`OP_QUERY` legacy)

Typical command routed through `admin.$cmd`:

```javascript
{ isMaster: 1 }
```

or newer equivalent:

```javascript
{ hello: 1 }
```

## 4) Legacy responses: `OP_REPLY` (`opCode = 1`)

Used as the response opcode for `OP_QUERY`.

### `OP_REPLY` layout (for reference)

- `responseFlags` (`int32`)
- `cursorID` (`int64`)
- `startingFrom` (`int32`)
- `numberReturned` (`int32`)
- `documents` (array of BSON docs)

## 5) Why this matters for proxy correctness

- Handshake timeouts commonly happen if parser-side inspection corrupts frame boundaries or over-reads strings/docs.
- CString handling must respect explicit decoded length (never scan past span bounds).
- `OP_QUERY` optional selector must not be treated as mandatory.
- For diagnostics, deserialized payload logging should be best-effort and never mutate forwarded bytes.

## 6) Current proxy behavior summary

- Parses/validates `OP_MSG` and `OP_QUERY` for inspection/logging.
- Forwards original bytes unchanged for all messages.
- Transparently forwards unknown opcodes.

This approach keeps backwards compatibility while still allowing structured introspection.
