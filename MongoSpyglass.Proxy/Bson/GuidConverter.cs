namespace MongoSpyglass.Proxy.Bson;

public unsafe class GuidConverter : IBlittableConverter<Guid>
{
    public static readonly GuidConverter Instance = new();
    public Guid Read(byte* p, BlittableBsonConstants.BsonType type, int length)
    {
        if (type != BlittableBsonConstants.BsonType.Binary) throw new InvalidCastException();
        int len = *(int*)p;
        byte subtype = p[4];
        var span = new ReadOnlySpan<byte>(p + 5, 16);
        return subtype == 4 ? new Guid(span, bigEndian: true) : new Guid(span);
    }
}
