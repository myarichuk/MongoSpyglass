# Rete-Based Analyzers Integration Design

## Overview
Replace the current concurrent dictionary-based analyzer plugins (`CursorAnalyzer`, `SlowQueryAnalyzer`) with a Rete algorithm-based rules engine using `NRules`.

## Architecture: Single Global Rete Network
- A **Single Rete Network (Global Session)** will be used to evaluate all traffic.
- Traffic will be funneled from the highly concurrent proxy hot path into a single `Channel<MessageFact>` to decouple rule evaluation from the hot path.
- A dedicated background worker thread will read from the channel and synchronously call `ISession.Insert()`, `Update()`, `Retract()`, and `Fire()`, ensuring thread safety for the NRules session without complex locking.

## Near Zero-Allocation Fact Management
- `ObservedMessage` is a struct pointing to zero-allocation pooled memory (`ArenaTracker`).
- NRules requires reference type facts. To avoid GC pressure, we will use an `ObjectPool<MessageFact>`.
- **Insertion Flow**: When a message arrives, we `Rent()` a `MessageFact` from the pool, set the `ObservedMessage`, call `AddRef()` (to prevent memory release), and insert it into the session.
- **Retraction Flow**: When a fact is retracted (e.g., matched by a rule or expired), we call `msg.Release()` and `Return()` the wrapper to the pool.

## Fact Expiration (30-Second TTL)
- NRules does not natively support TTL.
- A background timer will periodically update a `TimeTick` fact in the ISession (e.g., every 1 second).
- A generic `CleanupRule` will match any `MessageFact` where `Age > 30 seconds`.
- The action of this rule will be to `Retract()` the fact, trigger `Release()`, and recycle the wrapper.

## Analyzers as Rule Sets
- `IAnalyzerPlugin` will be adapted or replaced. Analyzers will instead provide collections of `NRules.Rule` classes.
- E.g., `SlowQueryResponseRule`, `CursorAbandonedRule`.
- The results of rules (Insights) can be accumulated in a thread-safe collection (e.g., `ConcurrentQueue<Insight>`) accessible by the UI.