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
