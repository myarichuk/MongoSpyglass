# Design Doc: Cursor Analyzer Improvements & Statistics

**Status:** Draft
**Date:** 2026-05-10
**Author:** Gemini CLI

## 1. Context & Problem Statement
The current cursor analyzer in MongoSpyglass detects cursor creation, `getMore` usage, and closure (via exhaustion or `killCursors`). However, it has several technical deficiencies:
- **Inefficiency:** `KillCursorsRule` performs a cross-join between messages and all open cursors, leading to $O(N*M)$ complexity in the rule engine.
- **Memory Pressure:** `TrackRequestRule` clones BSON payloads into `byte[]` for every request, even if they aren't "slow".
- **Missing Metrics:** There is no tracking of how long cursors remain open or reporting of average durations.
- **Precision:** Connection state matching for cursors is loose, potentially leading to incorrect state in multi-connection scenarios.

## 2. Objectives
1. **Refactor for Performance:** Use efficient NRules matching patterns for cursor lifecycle.
2. **Optimize Memory:** Leverage the `ArenaTracker` and `ObservedMessage` references instead of cloning BSON prematurely.
3. **Track Statistics:** Implement a sliding-window average for "Cursor Open Time".
4. **Dashboard Integration:** Add a "Cursor Statistics" widget to the Analysis page.

## 3. Proposed Architecture (Approach 1: Rete-Integrated Stats)

### 3.1 Data Models (Facts)

#### `CursorFact` (Modified)
- Add `ConnectionId` and `Namespace` matching.
- Capture `StartTime` from `MessageFact.Timestamp`.
- Ensure `ClosedAt` is set for all closure paths.

#### `CursorStatsFact` (New)
- `MaxWindowSize`: 100.
- `Durations`: `Queue<double>` (sliding window).
- `AverageOpenTimeMs`: Calculated value.
- `ActiveCount`: Derived from Rete query or tracked counter.

#### `PendingKillFact` (New)
- `CursorId`: long.
- `ConnectionId`: string.
- Acts as an intermediary to avoid cross-joins.

### 3.2 Rules

#### `DetectNewCursorRule` (Modified)
- Initialize `CursorFact` with `StartTime` from the message timestamp.

#### `ExplodeKillCursorsRule` (New)
- Matches a `killCursors` message.
- Inserts one `PendingKillFact` for every cursor ID in the message array.

#### `KillCursorsRule` (Modified)
- Matches a `PendingKillFact` and a `CursorFact` by `Id` and `ConnectionId`.
- Marks cursor as closed.

#### `UpdateCursorStatsRule` (New)
- Matches a `CursorFact` where `IsClosed == true`.
- Updates `CursorStatsFact` (adds duration to queue, pops if size exceeded).

#### `TrackRequestRule` (Memory Optimized)
- Instead of cloning the BSON payload into `byte[] RawBson` immediately, the `PendingRequestFact` will store a reference to the `MessageFact`.
- The BSON will only be serialized to JSON/cloned if `DetectSlowQueryRule` triggers (latency > 100ms).
- This ensures that 99% of requests (fast ones) never allocate a copy of their payload in the analyzer.

## 4. UI Design: Cursor Statistics Widget

### 4.1 Component: `Analysis.razor`
- Add a new "Cursor Health" section.
- **Active Cursors**: Live count.
- **Average Life Span**: The sliding window average.
- **Closure Distribution**: Text-based or small bar showing reasons (Exhaustion, Kill, Abandoned).

## 5. Verification Plan
- **Unit Tests**:
    - Verify `KillCursorsRule` works with multiple cursors in one message.
    - Verify `CursorStatsFact` sliding window math.
    - Verify memory release in `CleanupRule` still works correctly.
- **Manual Verification**:
    - Observe dashboard during a simulation of short-lived and long-lived cursors.
