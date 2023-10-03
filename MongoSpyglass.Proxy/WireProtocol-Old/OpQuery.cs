using System.Runtime.InteropServices;
using MongoDB.Bson;

namespace MongoSpyglass.Proxy.WireProtocol
{
    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    internal unsafe struct OpQuery
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
        public byte* pFullCollectionName;         // "dbname.collectionname"
        public int FullCollectionNameLength;

        public string? FullCollectionName => 
            Utils.PtrToStringUtf8(pFullCollectionName, FullCollectionNameLength);

        public int NumberToSkip;                 // number of documents to skip
        public int NumberToReturn;               // number of documents to return in the first OP_REPLY batch
        public byte* pQuery;                      // query object. BSON document
        public int QueryLength;

        public BsonDocument Query => Utils.BytePtrToBsonDocument(pQuery, QueryLength);

        public byte* pReturnFieldsSelector;       // Optional. Selector indicating the fields to return. BSON document.
        public int ReturnFieldsSelectorLength;
    }

}
