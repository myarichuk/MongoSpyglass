namespace MongoSpyglass.Service.Data;

public class AppSettings
{
    public int ProxyPort { get; set; } = 27017;
    public string MongoDbUrl { get; set; } = "127.0.0.1:27018";
    public bool RavenEmbedded { get; set; } = true;
    public string? RavenRemoteUrl { get; set; }
    public string RavenDatabase { get; set; } = "MongoSpyglass";
    public int SlowQueryThresholdMs { get; set; } = 100;
}

public class SettingsService
{
    private AppSettings _current = new();
    public AppSettings Current => _current;

    public event Action? OnSettingsChanged;

    public void Update(AppSettings settings)
    {
        _current = settings;
        OnSettingsChanged?.Invoke();
        // Future: Save to RavenDB or appsettings.json
    }
}
