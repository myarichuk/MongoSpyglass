using System.Net;

namespace Spyglass.Collector;

/// <summary>
/// Backoff for password guessing (03 §9, CA-20): after 5 failures from one address it is refused for 2^(n-6) s (at most 15 min);
/// over 60 failures a minute from anywhere, every address is refused until the minute is out. A success clears the address.
/// </summary>
internal sealed class LoginThrottle(Func<long>? nowMs = null)
{
    private const int FreeAttempts = 5, GlobalPerMinute = 60, MaxTracked = 10_000;
    private sealed class Entry { public int Failures; public long LockedUntilMs; public long LastMs; }

    private readonly Dictionary<IPAddress, Entry> _byIp = [];
    private readonly Func<long> _nowMs = nowMs ?? (() => Environment.TickCount64);
    private long _windowStartMs;
    private int _windowFailures;

    /// <summary>Whether an attempt from <paramref name="ip"/> is refused now, and for how many more seconds.</summary>
    public bool Locked(IPAddress? ip, out int retryAfterSeconds)
    {
        lock (_byIp)
        {
            var now = _nowMs();
            long until = 0;
            if (_windowFailures >= GlobalPerMinute && now - _windowStartMs < 60_000) until = _windowStartMs + 60_000;
            if (ip != null && _byIp.TryGetValue(Key(ip), out var e) && e.LockedUntilMs > until) until = e.LockedUntilMs;
            retryAfterSeconds = until > now ? (int)((until - now + 999) / 1000) : 0;
            return retryAfterSeconds > 0;
        }
    }

    public void Failed(IPAddress? ip)
    {
        lock (_byIp)
        {
            var now = _nowMs();
            if (_windowFailures == 0 || now - _windowStartMs >= 60_000) { _windowStartMs = now; _windowFailures = 0; }
            _windowFailures++;
            if (ip == null) return;
            if (_byIp.Count >= MaxTracked)
                foreach (var k in _byIp.Where(kv => kv.Value.LockedUntilMs < now && now - kv.Value.LastMs > 600_000).Select(kv => kv.Key).ToList()) _byIp.Remove(k);
            var key = Key(ip);
            if (!_byIp.TryGetValue(key, out var e))
            {
                if (_byIp.Count >= MaxTracked) return; // a flood of distinct addresses: the global window still applies
                _byIp[key] = e = new Entry();
            }
            e.LastMs = now;
            if (++e.Failures > FreeAttempts)
                e.LockedUntilMs = now + Math.Min(900_000L, 1000L << Math.Min(20, e.Failures - FreeAttempts - 1));
        }
    }

    public void Succeeded(IPAddress? ip)
    {
        if (ip == null) return;
        lock (_byIp) _byIp.Remove(Key(ip));
    }

    private static IPAddress Key(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
