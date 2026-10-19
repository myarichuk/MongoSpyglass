using System.Collections;
using System.Reflection;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;

namespace Spyglass.Proxy.Tests.Infrastructure;

/// <summary>
/// The driver keeps every cluster in a process-wide registry, and <see cref="MongoClient.Dispose()"/> does not
/// remove it: a client from an earlier test keeps monitoring (and reconnecting to) a proxy that is gone.
/// Measurement tests start from a quiet process by disposing all of them.
/// </summary>
public static class Clusters
{
    public static int DisposeAll()
    {
        var registry = ClusterRegistry.Instance;
        // the registry's map is private; find it by type, not by name
        var map = typeof(ClusterRegistry).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(f => f.GetValue(registry))
            .OfType<IDictionary>()
            .Single();
        List<ICluster> clusters;
        lock (map) clusters = map.Values.OfType<ICluster>().ToList(); // the registry locks something; the snapshot is all we need
        foreach (var c in clusters)
            registry.UnregisterAndDisposeCluster(c);
        return clusters.Count;
    }
}
