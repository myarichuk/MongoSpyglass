# Rete Analyzers Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace dictionary-based analyzers with a single global NRules Rete session for near zero-allocation rules evaluation.

**Architecture:** A single `ReteAnalyzerEngine` implements `ITrafficListener`, mapping `ObservedMessage` to a pooled `MessageFact`, and queuing it into a `Channel`. A background thread processes the queue, inserting/updating an NRules `ISession` and running a 30s TTL cleanup.

**Tech Stack:** C#, .NET 8, NRules

---
### Task 1: Setup NRules and Service Tests

**Files:**
- Create: `MongoSpyglass.Service.Tests/MongoSpyglass.Service.Tests.csproj`
- Modify: `MongoSpyglass.sln`

- [ ] **Step 1: Add NRules to Service**

```bash
dotnet add MongoSpyglass.Service/MongoSpyglass.Service.csproj package NRules
```

- [ ] **Step 2: Create Test Project**

```bash
dotnet new xunit -n MongoSpyglass.Service.Tests -o MongoSpyglass.Service.Tests
dotnet add MongoSpyglass.Service.Tests/MongoSpyglass.Service.Tests.csproj reference MongoSpyglass.Service/MongoSpyglass.Service.csproj
dotnet add MongoSpyglass.Service.Tests/MongoSpyglass.Service.Tests.csproj reference MongoSpyglass.Proxy/MongoSpyglass.Proxy.csproj
dotnet add MongoSpyglass.Service.Tests/MongoSpyglass.Service.Tests.csproj package NRules
dotnet sln MongoSpyglass.sln add MongoSpyglass.Service.Tests/MongoSpyglass.Service.Tests.csproj
```

- [ ] **Step 3: Run baseline test**

```bash
dotnet test MongoSpyglass.Service.Tests
```
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add MongoSpyglass.sln MongoSpyglass.Service/MongoSpyglass.Service.csproj MongoSpyglass.Service.Tests/
git commit -m "chore: setup NRules and MongoSpyglass.Service.Tests project"
```

---
### Task 2: Core Facts and Pool

**Files:**
- Create: `MongoSpyglass.Service/Analyzers/Rete/MessageFact.cs`
- Create: `MongoSpyglass.Service/Analyzers/Rete/TimeTick.cs`
- Test: `MongoSpyglass.Service.Tests/Analyzers/MessageFactTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// MongoSpyglass.Service.Tests/Analyzers/MessageFactTests.cs
using MongoSpyglass.Service.Analyzers.Rete;
using System;
using Xunit;

namespace MongoSpyglass.Service.Tests.Analyzers;

public class MessageFactTests
{
    [Fact]
    public void MessageFact_AgeSeconds_IsCalculated()
    {
        var fact = new MessageFact();
        fact.Timestamp = DateTime.UtcNow.AddSeconds(-10);
        Assert.True(fact.AgeSeconds >= 10);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test MongoSpyglass.Service.Tests/MongoSpyglass.Service.Tests.csproj --filter MessageFactTests
```
Expected: FAIL (missing classes)

- [ ] **Step 3: Write minimal implementation**

```csharp
// MongoSpyglass.Service/Analyzers/Rete/MessageFact.cs
using System;
using MongoSpyglass.Proxy;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class MessageFact
{
    public ObservedMessage Message { get; set; }
    public DateTime Timestamp { get; set; }
    public int AgeSeconds => (int)(DateTime.UtcNow - Timestamp).TotalSeconds;

    public void Clear()
    {
        Message = default;
        Timestamp = default;
    }
}
```

```csharp
// MongoSpyglass.Service/Analyzers/Rete/TimeTick.cs
using System;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class TimeTick
{
    public DateTime CurrentTime { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test MongoSpyglass.Service.Tests/MongoSpyglass.Service.Tests.csproj --filter MessageFactTests
```
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add MongoSpyglass.Service/Analyzers/Rete/ MongoSpyglass.Service.Tests/Analyzers/
git commit -m "feat: add MessageFact and TimeTick for Rete engine"
```

---
### Task 3: Base Rule and Cleanup Rule

**Files:**
- Create: `MongoSpyglass.Service/Analyzers/Rete/CleanupRule.cs`
- Test: `MongoSpyglass.Service.Tests/Analyzers/CleanupRuleTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// MongoSpyglass.Service.Tests/Analyzers/CleanupRuleTests.cs
using MongoSpyglass.Service.Analyzers.Rete;
using NRules;
using NRules.Fluent;
using System;
using System.Linq;
using Xunit;

namespace MongoSpyglass.Service.Tests.Analyzers;

public class CleanupRuleTests
{
    [Fact]
    public void CleanupRule_RetractsOldFacts()
    {
        var repository = new RuleRepository();
        repository.Load(x => x.From(typeof(CleanupRule).Assembly));
        var factory = repository.Compile();
        var session = factory.CreateSession();

        var oldFact = new MessageFact { Timestamp = DateTime.UtcNow.AddSeconds(-35) };
        var newFact = new MessageFact { Timestamp = DateTime.UtcNow };
        
        session.Insert(oldFact);
        session.Insert(newFact);
        session.Insert(new TimeTick());
        
        session.Fire();
        
        var remaining = session.Query<MessageFact>().ToList();
        Assert.Single(remaining);
        Assert.Equal(newFact, remaining[0]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test MongoSpyglass.Service.Tests/MongoSpyglass.Service.Tests.csproj --filter CleanupRuleTests
```
Expected: FAIL

- [ ] **Step 3: Write minimal implementation**

```csharp
// MongoSpyglass.Service/Analyzers/Rete/CleanupRule.cs
using NRules.Fluent.Dsl;
using System;
using Microsoft.Extensions.ObjectPool;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class CleanupRule : Rule
{
    public override void Define()
    {
        MessageFact fact = null;
        TimeTick tick = null;

        When()
            .Match<TimeTick>(() => tick)
            .Match<MessageFact>(() => fact, f => (tick.CurrentTime - f.Timestamp).TotalSeconds > 30);

        Then()
            .Do(ctx => RetractAndRelease(ctx, fact));
    }

    private void RetractAndRelease(IContext ctx, MessageFact fact)
    {
        ctx.Retract(fact);
        if (!fact.Message.IsDefault)
        {
            fact.Message.Release();
        }
        // Wrapper recycling happens in the engine when Retract is observed or explicitly managed.
        // For now, we clear the message reference.
        fact.Clear();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test MongoSpyglass.Service.Tests/MongoSpyglass.Service.Tests.csproj --filter CleanupRuleTests
```
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add MongoSpyglass.Service/Analyzers/Rete/CleanupRule.cs MongoSpyglass.Service.Tests/Analyzers/CleanupRuleTests.cs
git commit -m "feat: add TTL CleanupRule for NRules"
```

---
### Task 4: ReteAnalyzerEngine (Channel & Session)

**Files:**
- Create: `MongoSpyglass.Service/Analyzers/ReteAnalyzerEngine.cs`
- Modify: `MongoSpyglass.Service/Program.cs`

- [ ] **Step 1: Write implementation for Engine**

```csharp
// MongoSpyglass.Service/Analyzers/ReteAnalyzerEngine.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.ObjectPool;
using MongoSpyglass.Proxy;
using MongoSpyglass.Service.Analyzers.Rete;
using NRules;
using NRules.Fluent;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MongoSpyglass.Service.Analyzers;

public class ReteAnalyzerEngine : BackgroundService, ITrafficListener
{
    private readonly Channel<MessageFact> _channel = Channel.CreateUnbounded<MessageFact>();
    private readonly ISession _session;
    private readonly ObjectPool<MessageFact> _pool;
    private readonly ConcurrentQueue<Insight> _insights = new();
    private readonly TimeTick _tick = new();

    public ReteAnalyzerEngine()
    {
        var repository = new RuleRepository();
        repository.Load(x => x.From(typeof(CleanupRule).Assembly));
        var factory = repository.Compile();
        _session = factory.CreateSession();
        _pool = ObjectPool.Create(new DefaultPooledObjectPolicy<MessageFact>());
    }

    public string Name => "Rete Analyzer Engine";

    public IEnumerable<Insight> GetInsights() => _insights.ToArray();

    public void OnMessage(in ObservedMessage msg)
    {
        var fact = _pool.Get();
        fact.Message = msg;
        fact.Timestamp = DateTime.UtcNow;
        
        // AddRef to keep arena memory alive while in Rete
        if (!fact.Message.IsDefault)
        {
            fact.Message.AddRef();
        }

        if (!_channel.Writer.TryWrite(fact))
        {
            if (!fact.Message.IsDefault) fact.Message.Release();
            fact.Clear();
            _pool.Return(fact);
        }
    }

    public void OnConnectionClosed(string connectionId)
    {
        // Could insert a ConnectionClosedFact
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _session.Insert(_tick);
        
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        
        while (!stoppingToken.IsCancellationRequested)
        {
            bool hasItems = false;
            while (_channel.Reader.TryRead(out var fact))
            {
                _session.Insert(fact);
                hasItems = true;
            }

            if (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _tick.CurrentTime = DateTime.UtcNow;
                _session.Update(_tick);
                hasItems = true;
            }

            if (hasItems)
            {
                _session.Fire();
            }
        }
    }
}
```

- [ ] **Step 2: Wire up in Program.cs**

Modify `MongoSpyglass.Service/Program.cs` to remove the old plugins and register the engine.

```csharp
// In Program.cs (Find existing analyzer registrations and replace):
// builder.Services.AddSingleton<IAnalyzerPlugin, CursorAnalyzer>();
// builder.Services.AddSingleton<IAnalyzerPlugin, SlowQueryAnalyzer>();
builder.Services.AddSingleton<ReteAnalyzerEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReteAnalyzerEngine>());
builder.Services.AddSingleton<ITrafficListener>(sp => sp.GetRequiredService<ReteAnalyzerEngine>());
```

- [ ] **Step 3: Build & verify**

```bash
dotnet build MongoSpyglass.Service
```

- [ ] **Step 4: Commit**

```bash
git add MongoSpyglass.Service/Analyzers/ReteAnalyzerEngine.cs MongoSpyglass.Service/Program.cs
git commit -m "feat: implement ReteAnalyzerEngine and wire it into DI"
```
