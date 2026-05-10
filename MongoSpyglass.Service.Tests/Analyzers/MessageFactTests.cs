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
