using System.Collections.Generic;

namespace MongoSpyglass.Service.Analyzers;

public interface IAnalyzerPlugin : Proxy.ITrafficListener
{
    string Name { get; }
    IEnumerable<Insight> GetInsights();
}
