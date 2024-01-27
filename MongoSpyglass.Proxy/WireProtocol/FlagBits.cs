// Ignore Spelling: Mongo

namespace MongoSpyglass.Proxy.WireProtocol
{
    [Flags]
    public enum FlagBits : uint
    {
        None = 0,
        ChecksumPresent = 1 << 0,
        MoreToCome = 1 << 1,
        ExhaustAllowed = 1 << 16
    }
}
