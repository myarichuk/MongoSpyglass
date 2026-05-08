namespace MongoSpyglass.Service.Analyzers;

public enum InsightLevel { Info, Warning, Critical }
public record Insight(string Title, string Description, InsightLevel Level, string? Details = null);
