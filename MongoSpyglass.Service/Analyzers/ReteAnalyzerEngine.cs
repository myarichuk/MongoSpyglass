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
    private readonly ObjectPool<MessageFact> _pool;
    private readonly ConcurrentQueue<Insight> _insights = new();
    private readonly TimeTick _tick = new();

    public ReteAnalyzerEngine(RavenStorageService ravenService)
    {
        var repository = new RuleRepository();
        repository.Load(x => x.From(typeof(CleanupRule).Assembly));
        var factory = repository.Compile();
        _session = factory.CreateSession();
        _session.Insert(new CursorStatsFact());
        _pool = ObjectPool.Create(new DefaultPooledObjectPolicy<MessageFact>());

        ravenService.OnSessionChanged += (sessionId) => {
            _insights.Clear();
            // Clear facts in session
            var facts = _session.Query<object>().ToList();
            foreach (var f in facts) _session.Retract(f);
            _session.Insert(_tick);
            _session.Insert(new CursorStatsFact());
        };
    }

    public string Name => "Rete Analyzer Engine";

    public IEnumerable<Insight> GetInsights() => _insights.ToArray();

    public int ActiveCursorsCount => _session.Query<CursorFact>().Count(x => !x.IsClosed);

    public CursorStatsFact GetCursorStats() => _session.Query<CursorStatsFact>().FirstOrDefault() ?? new CursorStatsFact();

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
        _session.Insert(new ConnectionClosedFact { ConnectionId = connectionId });
        _session.Fire();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _session.Insert(_tick);
        
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        
        while (!stoppingToken.IsCancellationRequested)
        {
            bool hasItems = false;
            
            // Drain the channel and insert into session
            while (_channel.Reader.TryRead(out var fact))
            {
                _session.Insert(fact);
                hasItems = true;
            }

            // Periodic tick for TTL
            if (timer.WaitForNextTickAsync(stoppingToken).IsCompleted)
            {
                _tick.CurrentTime = DateTime.UtcNow;
                _session.Update(_tick);
                hasItems = true;
            }
            else if (!hasItems)
            {
                try {
                    await _channel.Reader.WaitToReadAsync(stoppingToken);
                } catch (OperationCanceledException) { break; }
                continue;
            }

            if (hasItems)
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
