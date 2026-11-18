using System.Net;

namespace Spyglass.Collector;

/// <summary>
/// Backoff for password guessing (03 §9, CA-20, FC-2). Keyed by client address (IPv6 by /64; the caller derives the address,
/// believing <c>X-Forwarded-For</c> only from a trusted proxy): after 5 failures an address is refused for 2^(n-6) s (at most
/// 15 min); a quiet period of 10 min forgets its failures and a success clears them. One address's failures never refuse
/// another address. Failures from anywhere beyond 60 a minute only add a delay to the answer (<see cref="ExtraDelayMs"/>),
/// they never lock a correct password out.
/// </summary>
internal sealed class LoginThrottle(Func<long>? nowMs = null)
{
    private const int FreeAttempts = 5, GlobalPerMinute = 60, MaxTracked = 10_000;
    private const long ForgetMs = 600_000;
    private sealed class Entry { public int Failures; public long LockedUntilMs; public long LastMs; }

    private readonly Dictionary<IPAddress, Entry> _byIp = [];
    private readonly Func<long> _nowMs = nowMs ?? (() => Environment.TickCount64);
    private long _windowStartMs;
    private int _windowFailures;

    /// <summary>Whether an attempt from <paramref name="ip"/> is refused now, and for how many more seconds.</summary>
    public bool Locked(IPAddress? ip, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (ip == null) return false;
        lock (_byIp)
        {
            var now = _nowMs();
            if (!_byIp.TryGetValue(Key(ip), out var e) || e.LockedUntilMs <= now) return false;
            retryAfterSeconds = (int)((e.LockedUntilMs - now + 999) / 1000);
            return true;
        }
    }

    /// <summary>A short extra wait for a failed attempt while failures from all addresses run above the global rate.</summary>
    public int ExtraDelayMs()
    {
        lock (_byIp) return _windowFailures >= GlobalPerMinute && _nowMs() - _windowStartMs < 60_000 ? 2000 : 0;
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
                foreach (var k in _byIp.Where(kv => kv.Value.LockedUntilMs < now && now - kv.Value.LastMs > ForgetMs).Select(kv => kv.Key).ToList()) _byIp.Remove(k);
            var key = Key(ip);
            if (!_byIp.TryGetValue(key, out var e))
            {
                if (_byIp.Count >= MaxTracked) return; // a flood of distinct addresses: the global delay still applies
                _byIp[key] = e = new Entry();
            }
            if (e.LockedUntilMs <= now && now - e.LastMs > ForgetMs) e.Failures = 0; // decay (FC-21): an old burst is forgotten
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

    /// <summary>IPv4-mapped IPv6 is the IPv4 address; any other IPv6 address stands for its /64 (one subscriber holds the whole prefix).</summary>
    internal static IPAddress Key(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip;
        Span<byte> b = stackalloc byte[16];
        ip.TryWriteBytes(b, out _);
        b[8..].Clear();
        return new IPAddress(b);
    }
}
