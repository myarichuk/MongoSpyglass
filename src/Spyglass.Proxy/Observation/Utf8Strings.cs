using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace Spyglass.Proxy.Observation;

/// <summary>
/// UTF-8 bytes → value, probed by span without allocating (.NET alternate lookup). Bounded: once full it
/// stops admitting new keys, so client-chosen names cannot grow it; misses past the bound cost what they
/// cost before the table existed. Hashing is <see cref="HashCode"/>'s, seeded per process.
/// Single-threaded: one per observer worker.
/// </summary>
internal sealed class Utf8Table<TValue>
{
    public const int MaxKeyBytes = 256;

    private sealed class Comparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static readonly Comparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] key) => GetHashCode((ReadOnlySpan<byte>)key);
        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);
        public int GetHashCode(ReadOnlySpan<byte> alternate)
        {
            var h = new HashCode();
            h.AddBytes(alternate);
            return h.ToHashCode();
        }
        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
    }

    private readonly Dictionary<byte[], TValue> _map = new(Comparer.Instance);
    private readonly Dictionary<byte[], TValue>.AlternateLookup<ReadOnlySpan<byte>> _lookup;
    private readonly int _capacity;

    public Utf8Table(int capacity)
    {
        _capacity = capacity;
        _lookup = _map.GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    public int Count => _map.Count;

    public bool TryGetValue(ReadOnlySpan<byte> key, [MaybeNullWhen(false)] out TValue value) => _lookup.TryGetValue(key, out value);

    /// <summary>Admits the key if there is room; returns whether it did.</summary>
    public bool TryAdd(ReadOnlySpan<byte> key, TValue value) =>
        key.Length <= MaxKeyBytes && _map.Count < _capacity && _lookup.TryAdd(key, value);
}

/// <summary>Interned strings for names that repeat across operations: commands, databases, collections, error codes.</summary>
internal sealed class Utf8Interner(int capacity = 4096)
{
    private readonly Utf8Table<string> _table = new(capacity);

    public int Count => _table.Count;

    public string Get(ReadOnlySpan<byte> utf8)
    {
        if (_table.TryGetValue(utf8, out var s))
            return s;
        s = Encoding.UTF8.GetString(utf8);
        _table.TryAdd(utf8, s);
        return s;
    }
}

/// <summary>
/// 01 §9.2 lsid hashes, cached by session id. Drivers pool server sessions, so the same few hundred ids
/// repeat for the life of a process; SHA-256 through the platform provider is microseconds a call, a
/// dictionary hit is nanoseconds. Cleared, not evicted, when full: a one-off re-hash is cheaper than LRU
/// bookkeeping on every hit. Single-threaded: one per observer worker.
/// </summary>
internal sealed class LsidHasher(int capacity = 8192)
{
    private readonly Dictionary<UInt128, string> _cache = new();

    public int Count => _cache.Count;

    public string Hash(ReadOnlySpan<byte> uuid)
    {
        if (uuid.Length != 16)
            return RequestParser.HashLsidBytes(uuid);
        var key = MemoryMarshal.Read<UInt128>(uuid);
        if (_cache.TryGetValue(key, out var s))
            return s;
        if (_cache.Count >= capacity)
            _cache.Clear();
        return _cache[key] = RequestParser.HashLsidBytes(uuid);
    }

    /// <summary><c>lsid</c> document → hash of its <c>id</c>, or null if there is no binary id.</summary>
    public string? HashDocument(ReadOnlySpan<byte> lsidDoc)
    {
        foreach (var e in Wire.Bson.Elements(lsidDoc))
        {
            if (!e.NameIs("id"u8) || e.Type != Wire.BsonType.Binary || e.Value.Length < 5) continue;
            return Hash(e.Value[5..]);
        }
        return null;
    }
}
