using System.Collections.Concurrent;
using MongoSpyglass.Service.Analyzers;

namespace MongoSpyglass.Service.Data;

public class NotificationHubService
{
    private List<MongoInsight> _notifications = new();
    private readonly IEnumerable<IAnalyzerPlugin> _analyzers;
    private readonly RavenStorageService _ravenService;
    private readonly object _lock = new();

    public event Action? OnNotificationsUpdated;

    public NotificationHubService(IEnumerable<IAnalyzerPlugin> analyzers, RavenStorageService ravenService)
    {
        _analyzers = analyzers;
        _ravenService = ravenService;
        
        _ravenService.OnSessionChanged += (sessionId) => {
            lock (_lock)
            {
                _notifications.Clear();
            }
            OnNotificationsUpdated?.Invoke();
        };

        // Initial load
        Task.Run(async () => {
            _notifications = await _ravenService.GetInsightsAsync();
            OnNotificationsUpdated?.Invoke();
        });
    }

    public void Refresh()
    {
        lock (_lock)
        {
            var allInsights = _analyzers.SelectMany(a => a.GetInsights()).ToList();
            bool updated = false;

            foreach (var insight in allInsights)
            {
                if (!_notifications.Any(n => n.Title == insight.Title && n.Timestamp == insight.Timestamp))
                {
                    var mongoInsight = new MongoInsight
                    {
                        Title = insight.Title,
                        Description = insight.Description,
                        Level = insight.Level.ToString(),
                        Category = insight.Category,
                        Details = insight.Details ?? "",
                        Timestamp = insight.Timestamp
                    };

                    _notifications.Add(mongoInsight);
                    _ = _ravenService.StoreInsightAsync(mongoInsight);
                    updated = true;
                }
            }
            
            if (updated)
            {
                // Limit to last 100
                if (_notifications.Count > 100)
                {
                    _notifications = _notifications.OrderByDescending(x => x.Timestamp).Take(100).ToList();
                }
                
                OnNotificationsUpdated?.Invoke();
            }
        }
    }

    public async Task DismissAsync(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;

        lock (_lock)
        {
            var found = _notifications.FirstOrDefault(n => n.Id == id);
            if (found != null)
            {
                _notifications.Remove(found);
            }
        }

        await _ravenService.DeleteInsightAsync(id);
        OnNotificationsUpdated?.Invoke();
    }

    public async Task DismissAllAsync()
    {
        lock (_lock)
        {
            _notifications.Clear();
        }

        await _ravenService.DeleteAllInsightsAsync();
        OnNotificationsUpdated?.Invoke();
    }

    public List<MongoInsight> GetAll()
    {
        lock (_lock) return _notifications.OrderByDescending(n => n.Timestamp).ToList();
    }

    public int UnreadCount => 0; // Simplified for now since we have dismissal

    public void MarkAllAsRead()
    {
        OnNotificationsUpdated?.Invoke();
    }

    public void MarkAsRead(MongoInsight insight)
    {
        OnNotificationsUpdated?.Invoke();
    }
}
