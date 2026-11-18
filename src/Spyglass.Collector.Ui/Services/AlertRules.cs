namespace Spyglass.Collector.Ui.Services;

/// <summary>One line per rule of 03 §5.2.</summary>
public static class AlertRules
{
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["slow-shape"] = "server latency over the threshold, or p95 at ≥ 3× the previous hour",
        ["n-plus-one"] = "one unit of work runs the same shape many times with different values (needs an exporter)",
        ["n-plus-one-suspected"] = "the same shape back to back on one connection: a query in a loop (no unit-of-work context)",
        ["repeated-query"] = "the same query with the same values, repeatedly, within a unit of work or a second",
        ["unbounded-scan"] = "no filter and no limit on a collection known to be large",
        ["large-result"] = "> 1 MiB or > 1000 documents in one reply",
        ["unbounded-in"] = "$in with more than 1000 values",
        ["cursor-abandoned"] = "a cursor the client stopped reading: the server expired it",
        ["cursor-not-exhausted"] = "a unit of work ended while a cursor it opened was still open (needs an exporter)",
        ["pool-starvation"] = "p95 connection pool wait over 10 ms, or checkouts timing out (needs an exporter)",
        ["client-overhead"] = "driver time outside the wire exchange dominates for many ops of a shape (needs an exporter)",
        ["error-burst"] = "> 5% of a command's ops failing in a minute",
        ["unacknowledged-writes"] = "w:0 writes: failures never reach the application",
        ["exporter-skew"] = "an exporter's clock is more than 1 s off the proxy's, or its joins conflict",
    };

    public static string Explain(string rule) => Descriptions.TryGetValue(rule, out var d) ? d : "";
}
