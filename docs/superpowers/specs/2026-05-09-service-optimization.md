# Design: High-Performance Service Optimization (Option 1)

## Goal
Transform the MongoSpyglass service layer into a high-throughput, low-allocation telemetry consumer by deferring expensive operations and batching database writes.

## Architecture

### 1. Centralized Correlation
Replace O(N) circular buffer scans with a high-speed lookup table for matching responses to requests.
- **Component:** `TrafficMonitorService`
- **Mechanism:** `ConcurrentDictionary<int, long> _pendingRequestTimestamps` (and other metadata) to correlate `ResponseTo` with original `RequestId`.

### 2. Zero-Allocation Telemetry Pipeline
Defer BSON parsing and JSON formatting until the last possible moment (UI request).
- **Component:** `DecryptedTraffic`
- **Change:** Store `byte[] RawBson` instead of `string PayloadJson`.
- **Change:** `TrafficMonitorService` will only extract basic metadata (Command, Collection) using `BlittableBsonDocument` offsets, avoiding full deserialization.

### 3. RavenDB Bulk Ingestion
Protect the database and thread pool from write saturation.
- **Component:** `RavenStorageService`
- **Mechanism:** Use a `Channel<(MongoOperation, byte[])>` to buffer incoming telemetry.
- **Worker:** A dedicated `BackgroundService` will consume the channel and use `BulkInsertOperation` to persist operations in high-speed batches.

### 4. Optimized Analyzers
- **SlowQueryAnalyzer:** Store raw BSON bytes for slow queries; perform `ToJson()` only when `GetInsights()` is called or when the detail is requested.

## Data Flow
1. `ObservedMessage` arrives in `TrafficMonitorService.OnMessage`.
2. Message is queued in `_incomingChannel`.
3. `ProcessMessagesAsync` extracts metadata (offsets only).
4. For "to" tags, metadata is stored in the Correlation Map.
5. For "from" tags, metadata is looked up in the Correlation Map to calculate duration.
6. Operation + Raw BSON bytes are sent to `RavenStorageService` bulk channel.
7. `BulkInsertWorker` flushes data to RavenDB.
8. UI (Blazor) receives `OnTrafficReceived` and requests JSON formatting for the visible row.

## Success Criteria
- [ ] No `BsonSerializer.Deserialize` calls in the `ProcessMessagesAsync` hot path.
- [ ] Request correlation changed from O(N) to O(1).
- [ ] Database writes are batched via `BulkInsertOperation`.
- [ ] Memory allocation per message reduced by >50%.
