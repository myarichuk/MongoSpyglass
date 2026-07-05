using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System;
using MongoSpyglass.Proxy;
using System.Net;

namespace MongoSpyglass.Service.Data;

public class AppSettings
{
    public string Id { get; set; } = "AppSettings/Default";
    public int ProxyPort { get; set; } = 27018; // Listening port (proxy receives on this)
    public string MongoDbUrl { get; set; } = "127.0.0.1:27017"; // Target MongoDB server
    public string BindAddress { get; set; } = "0.0.0.0"; // Bind address (0.0.0.0 = all interfaces, 127.0.0.1 = localhost only)
    public bool RavenEmbedded { get; set; } = true;
    public string? RavenRemoteUrl { get; set; }
    public string RavenDatabase { get; set; } = "MongoSpyglass";
    public int SlowQueryThresholdMs { get; set; } = 100;
    public int CursorLeakAlertThresholdHours { get; set; } = 1; // Alert on idle > 1 hour (cleanup at 24h)
    public int N1DetectionWindowMs { get; set; } = 5000; // 5-second window for N+1 detection
    public int N1CountThreshold { get; set; } = 5; // Alert if ≥5 similar queries in window
    public int RawDataRetentionHours { get; set; } = 24;
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
        _ravenService.RetentionHours = _current.RawDataRetentionHours;
    }

    public async Task UpdateAsync(AppSettings settings)
    {
        _current = settings;
        _ravenService.RetentionHours = _current.RawDataRetentionHours;
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

        IPAddress ipAddress;

        // Handle basic hostnames/IPs
        if (!IPAddress.TryParse(host, out ipAddress))
        {
            try
            {
                var ips = Dns.GetHostAddresses(host);
                if (ips.Length == 0)
                {
                    throw new InvalidOperationException($"DNS resolution failed for '{host}': no addresses returned");
                }
                ipAddress = ips[0];
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                throw new InvalidOperationException($"Failed to resolve MongoDB host '{host}': {ex.Message}", ex);
            }
        }

        return (new IPEndPoint(ipAddress, port), _current.ProxyPort);
    }

    public IPAddress GetBindAddress()
    {
        if (IPAddress.TryParse(_current.BindAddress, out var ipAddress))
        {
            return ipAddress;
        }

        throw new InvalidOperationException($"Invalid BindAddress setting: '{_current.BindAddress}' is not a valid IP address");
    }
}
