using System.Text;

namespace Spyglass.Collector.Util;

/// <summary>
/// UTF-8 bytes → string, probed by span without allocating (.NET alternate lookup). Bounded: once full it stops
/// admitting new keys, so producer-chosen names cannot grow it; a miss past the bound costs what it cost
/// before. Single-threaded: one per thread.
/// </summary>
internal sealed class Utf8Interner
{
    public const int MaxKeyBytes = 128;

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

    private readonly Dictionary<byte[], string> _map = new(Comparer.Instance);
    private readonly Dictionary<byte[], string>.AlternateLookup<ReadOnlySpan<byte>> _lookup;
    private readonly int _capacity;

    public Utf8Interner(int capacity = 4096)
    {
        _capacity = capacity;
        _lookup = _map.GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    public int Count => _map.Count;

    public string Get(ReadOnlySpan<byte> utf8)
    {
        if (_lookup.TryGetValue(utf8, out var s)) return s;
        s = Encoding.UTF8.GetString(utf8);
        if (utf8.Length <= MaxKeyBytes && _map.Count < _capacity) _lookup.TryAdd(utf8, s);
        return s;
    }
}
