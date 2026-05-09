namespace MongoSpyglass.Service.Analyzers;

public enum InsightLevel { Info, Warning, Critical }

public record Insight(
    string Title, 
    string Description, 
    InsightLevel Level, 
    string? Details = null, 
    DateTime? CreatedAt = null, 
    string Category = "General")
{
    public DateTime Timestamp { get; init; } = CreatedAt ?? DateTime.Now;
    public bool IsRead { get; set; }
}
