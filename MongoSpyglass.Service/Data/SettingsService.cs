using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System;
using MongoSpyglass.Proxy;
using System.Net;

namespace MongoSpyglass.Service.Data;

public class AppSettings
{
    public string Id { get; set; } = "AppSettings/Default";
    public int ProxyPort { get; set; } = 27017;
    public string MongoDbUrl { get; set; } = "127.0.0.1:27018";
    public bool RavenEmbedded { get; set; } = true;
    public string? RavenRemoteUrl { get; set; }
    public string RavenDatabase { get; set; } = "MongoSpyglass";
    public int SlowQueryThresholdMs { get; set; } = 100;
}

public class SettingsService : IProxySettingsProvider
{
    private AppSettings _current = new();
    private readonly RavenStorageService _ravenService;
    private readonly IConfiguration _config;
    
    public AppSettings Current => _current;
    public event Action? OnSettingsChanged;

    public SettingsService(RavenStorageService ravenService, IConfiguration config)
    {
        _ravenService = ravenService;
        _config = config;
    }

    public async Task InitializeAsync()
    {
        var storedSettings = await _ravenService.GetSettingsAsync();
        if (storedSettings != null)
        {
            _current = storedSettings;
        }
        else
        {
            // Seed from appsettings.json
            _current = new AppSettings
            {
                ProxyPort = int.TryParse(_config["Proxy:IncomingPort"], out var p) ? p : 27018,
                MongoDbUrl = $"{_config["Proxy:MongoDbServer"] ?? "127.0.0.1"}:{_config["Proxy:MongoDbPort"] ?? "27017"}"
            };
            await _ravenService.SaveSettingsAsync(_current);
        }
    }

    public async Task UpdateAsync(AppSettings settings)
    {
        _current = settings;
        await _ravenService.SaveSettingsAsync(_current);
        OnSettingsChanged?.Invoke();
    }

    public void Update(AppSettings settings)
    {
        _ = UpdateAsync(settings);
    }

    public (IPEndPoint TargetServer, int IncomingPort) GetCurrentSettings()
    {
        var parts = _current.MongoDbUrl.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 ? int.Parse(parts[1]) : 27017;
        
        // Handle basic hostnames/IPs
        if (!IPAddress.TryParse(host, out var ipAddress))
        {
            try {
                var ips = Dns.GetHostAddresses(host);
                ipAddress = ips.Length > 0 ? ips[0] : IPAddress.Loopback;
            } catch {
                ipAddress = IPAddress.Loopback;
            }
        }
        
        return (new IPEndPoint(ipAddress, port), _current.ProxyPort);
    }
}
