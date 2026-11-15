namespace Spyglass.Proxy.Wire;

public enum BsonKind : byte { Unknown, Zero, Fixed, Prefixed, Regex }

/// <summary>
/// The one BSON type → value-size table. <see cref="Bson.ValueSize"/> and the streaming scanners
/// (reply, request body fields) all frame values through it, so a rule (a new type, a minimum length) is fixed once.
/// </summary>
public static class BsonLayout
{
    /// <summary>
    /// How a value of <paramref name="type"/> is framed: <see cref="BsonKind.Zero"/> (no bytes),
    /// <see cref="BsonKind.Fixed"/> (<paramref name="fixedSize"/> bytes), <see cref="BsonKind.Prefixed"/> (an int32 length,
    /// then <see cref="PrefixedRest"/> bytes), <see cref="BsonKind.Regex"/> (two C strings) or <see cref="BsonKind.Unknown"/>.
    /// </summary>
    public static BsonKind Classify(byte type, out int fixedSize)
    {
        fixedSize = 0;
        switch (type)
        {
            case BsonType.Double:
            case BsonType.DateTime:
            case BsonType.Timestamp:
            case BsonType.Int64:
                fixedSize = 8;
                return BsonKind.Fixed;
            case BsonType.Int32:
                fixedSize = 4;
                return BsonKind.Fixed;
            case BsonType.Boolean:
                fixedSize = 1;
                return BsonKind.Fixed;
            case BsonType.ObjectId:
                fixedSize = 12;
                return BsonKind.Fixed;
            case BsonType.Decimal128:
                fixedSize = 16;
                return BsonKind.Fixed;
            case BsonType.Undefined:
            case BsonType.Null:
            case BsonType.MinKey:
            case BsonType.MaxKey:
                return BsonKind.Zero;
            case BsonType.String:
            case BsonType.JavaScript:
            case BsonType.Symbol:
            case BsonType.Document:
            case BsonType.Array:
            case BsonType.JavaScriptWithScope:
            case BsonType.Binary:
            case BsonType.DbPointer:
                return BsonKind.Prefixed;
            case BsonType.Regex:
                return BsonKind.Regex;
            default:
                return BsonKind.Unknown;
        }
    }

    /// <summary>
    /// For a <see cref="BsonKind.Prefixed"/> type whose int32 prefix is <paramref name="len"/>: the number of bytes after the
    /// prefix, or -1 if <paramref name="len"/> is not valid for the type.
    /// </summary>
    public static long PrefixedRest(byte type, int len) => type switch
    {
        BsonType.String or BsonType.JavaScript or BsonType.Symbol => len >= 1 ? len : -1,                 // includes the NUL
        BsonType.Document or BsonType.Array or BsonType.JavaScriptWithScope => len >= 5 ? len - 4L : -1,  // length includes the prefix
        BsonType.Binary => len >= 0 ? len + 1L : -1,                                                      // subtype byte + data
        BsonType.DbPointer => len >= 1 ? len + 12L : -1,                                                  // string + ObjectId
        _ => -1,
    };
}
