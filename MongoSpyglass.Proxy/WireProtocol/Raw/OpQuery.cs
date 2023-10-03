using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol.Raw
{
    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    internal ref struct OpQuery
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

        public OperationFlags Flags;

        public Span<char> FullCollectionName; //C-style string

        public int NumberToSkip;
        public int NumberToReturn;

        public Span<byte> Query; //BSON document
        public Span<byte> ReturnFieldsSelector; //BSON document
    }
}
