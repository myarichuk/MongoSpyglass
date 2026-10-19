namespace Spyglass.Proxy.Tests.Infrastructure;

/// <summary>
/// Measurement tests read process-global counters (allocated bytes, CPU time): they run after every
/// parallel collection, alone, so no other test's work is charged to the proxy.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Isolated
{
    public const string Name = "isolated";
}
