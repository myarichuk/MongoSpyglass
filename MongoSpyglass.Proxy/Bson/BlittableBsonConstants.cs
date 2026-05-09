namespace MongoSpyglass.Proxy.Bson;

public static class BlittableBsonConstants
{
    public const int DocumentHeaderSize = 4;
    public const byte DocumentTerminator = 0x00;

    public enum BsonType : byte
    {
        Double = 0x01,
        String = 0x02,
        Document = 0x03,
        Array = 0x04,
        Binary = 0x05,
        ObjectId = 0x07,
        Boolean = 0x08,
        DateTime = 0x09,
        Null = 0x0A,
        Symbol = 0x0E,
        CodeWithScope = 0x0F,
        Int32 = 0x10,
        Timestamp = 0x11,
        Int64 = 0x12,
        Decimal128 = 0x13,
        MinKey = 0xFF,
        MaxKey = 0x7F
    }
}
