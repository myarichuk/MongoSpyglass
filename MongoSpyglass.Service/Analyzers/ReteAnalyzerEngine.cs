using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.ObjectPool;
using MongoSpyglass.Proxy;
using MongoSpyglass.Service.Analyzers.Rete;
using MongoSpyglass.Service.Data;
using NRules;
using NRules.Fluent;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MongoSpyglass.Service.Analyzers;

public class ReteAnalyzerEngine : BackgroundService, IAnalyzerPlugin
{
    private readonly Channel<MessageFact> _channel = Channel.CreateUnbounded<MessageFact>();
    private readonly NRules.ISession _session;
    private readonly object _syncLock = new();
    private readonly ObjectPool<MessageFact> _pool;
    private readonly ConcurrentQueue<Insight> _insights = new();
    private readonly TimeTick _tick = new();
    private readonly RavenStorageService _ravenService;
    private bool _hydrated = false;

    public ReteAnalyzerEngine(RavenStorageService ravenService)
    {
        _ravenService = ravenService;
        var repository = new RuleRepository();
        repository.Load(x => x.From(typeof(CleanupRule).Assembly));
        var factory = repository.Compile();
        _session = factory.CreateSession();
        _pool = ObjectPool.Create(new DefaultPooledObjectPolicy<MessageFact>());

        ravenService.OnSessionChanged += (sessionId) => {
            _insights.Clear();
            lock (_syncLock)
            {
                // Clear facts in session
                var facts = _session.Query<object>().ToList();
                foreach (var f in facts) _session.Retract(f);
                _session.Insert(_tick);
                _hydrated = false;
            }
        };
    }

    public string Name => "Rete Analyzer Engine";

    public IEnumerable<Insight> GetInsights() => _insights.ToArray();

    public int ActiveCursorsCount
    {
        get
        {
            lock (_syncLock)
            {
                return _session.Query<CursorFact>().Count(x => !x.IsClosed);
            }
        }
    }

    public CursorStatsFact GetCursorStats()
    {
        lock (_syncLock)
        {
            return _session.Query<CursorStatsFact>().FirstOrDefault() ?? new CursorStatsFact();
        }
    }

    public void OnMessage(in ObservedMessage msg)
    {
        var fact = _pool.Get();
        fact.Message = msg;
        fact.Timestamp = DateTime.UtcNow;
        
        // AddRef to keep arena memory alive while in Rete
        if (fact.Message.Tracker != null)
        {
            fact.Message.AddRef();
        }

        if (!_channel.Writer.TryWrite(fact))
        {
            if (fact.Message.Tracker != null) fact.Message.Release();
            fact.Clear();
            _pool.Return(fact);
        }
    }

    public void OnConnectionClosed(string connectionId)
    {
        lock (_syncLock)
        {
            _session.Insert(new ConnectionClosedFact { ConnectionId = connectionId });
            _session.Fire();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_syncLock)
        {
            _session.Insert(_tick);
        }
        
        DateTime lastTick = DateTime.UtcNow;
        DateTime lastSave = DateTime.UtcNow;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_hydrated)
            {
                await HydrateAsync();
                _hydrated = true;
            }

            bool hasItems = false;
            
            // Drain the channel and insert into session
            while (_channel.Reader.TryRead(out var fact))
            {
                lock (_syncLock)
                {
                    _session.Insert(fact);
                }
                hasItems = true;
            }

            var now = DateTime.UtcNow;

            // Periodic tick for TTL
            if ((now - lastTick).TotalSeconds >= 1)
            {
                lock (_syncLock)
                {
                    _tick.CurrentTime = now;
                    _session.Update(_tick);
                }
                hasItems = true;
                lastTick = now;
            }

            // Periodic save
            if ((now - lastSave).TotalMinutes >= 1)
            {
                await SaveStateAsync();
                lastSave = now;
            }

            if (!hasItems && _channel.Reader.Count == 0)
            {
                try {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(1));
                    await _channel.Reader.WaitToReadAsync(cts.Token);
                } catch (OperationCanceledException) { }
                continue;
            }

            if (hasItems)
            {
                lock (_syncLock)
                {
                    _session.Fire();
                    
                    // Harvest insights produced by rules
                    var insights = _session.Query<Insight>().ToList();
                    foreach (var insight in insights)
                    {
                        _insights.Enqueue(insight);
                        _session.Retract(insight);
                        
                        // Limit insights
                        while (_insights.Count > 100) _insights.TryDequeue(out _);
                    }
                }
            }
        }
    }

    private async Task HydrateAsync()
    {
        var sessionId = _ravenService.ActiveSessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            lock (_syncLock)
            {
                _session.Insert(new CursorStatsFact());
            }
            return;
        }

        var stats = await _ravenService.GetLatestCursorStatsAsync(sessionId);
        var cursors = await _ravenService.GetActiveCursorsAsync(sessionId);

        lock (_syncLock)
        {
            _session.Insert(new SessionFact { Id = sessionId });
            _session.Insert(stats ?? new CursorStatsFact { SessionId = sessionId });

            foreach (var c in cursors)
            {
                _session.Insert(c);
            }
            
            _session.Fire();
        }
    }

    private async Task SaveStateAsync()
    {
        var sessionId = _ravenService.ActiveSessionId;
        if (string.IsNullOrEmpty(sessionId)) return;

        CursorStatsFact stats;
        List<CursorFact> activeCursors;

        lock (_syncLock)
        {
            stats = _session.Query<CursorStatsFact>().FirstOrDefault() ?? new CursorStatsFact { SessionId = sessionId };
            activeCursors = _session.Query<CursorFact>().Where(x => !x.IsClosed).ToList();
        }

        await _ravenService.StoreCursorStatsAsync(stats);

        foreach (var c in activeCursors)
        {
            c.SessionId = sessionId;
            await _ravenService.StoreCursorAsync(c);
        }
    }
}
