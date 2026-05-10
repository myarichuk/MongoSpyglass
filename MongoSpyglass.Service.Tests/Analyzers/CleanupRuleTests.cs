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
