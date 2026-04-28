namespace MongoSpyglass.Service.Data;

public record SlowQueryModel(
    string Namespace,
    string Operation,
    int AvgTimeMs,
    int Count,
    string Shape);

public record LatencyDataPoint(
    DateTime Time,
    double Reads,
    double Writes);

// Note: Additional view models would go here as the application develops.
// For now, these records match what was added to the @code block in Index.razor.
