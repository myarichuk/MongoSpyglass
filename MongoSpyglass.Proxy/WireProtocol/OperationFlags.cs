namespace MongoSpyglass.Proxy.WireProtocol
{
    [Flags]
    public enum OperationFlags : int
    {
        None = 0,                 // 0 is reserved. Must be set to 0
        TailableCursor = 1 << 0,  // 1 corresponds to TailableCursor
        SlaveOk = 1 << 1,         // 2 corresponds to SlaveOk
        OplogReplay = 1 << 2,     // 3 corresponds to OplogReplay
        NoCursorTimeout = 1 << 3, // 4 corresponds to NoCursorTimeout
        AwaitData = 1 << 4,       // 5 corresponds to AwaitData
        Exhaust = 1 << 5,         // 6 corresponds to Exhaust
        Partial = 1 << 6          // 7 corresponds to Partial
        // 8-31 are reserved. Must be set to 0.
    }
}
