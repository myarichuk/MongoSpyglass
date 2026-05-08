namespace MongoSpyglass.Proxy.Bson;

public unsafe interface IBlittableConverter<T>
{
    T Read(byte* p, BlittableBsonConstants.BsonType type, int length);
}

internal static class BlittableConverter<T>
{
    public static IBlittableConverter<T> Instance = default!; 
}
