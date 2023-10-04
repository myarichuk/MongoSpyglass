//using Simple.Arena;
//// ReSharper disable ComplexConditionExpression

//namespace MongoSpyglass.Proxy.WireProtocol
//{
//    internal class OpQueryParser: IMessageParser<OpQuery>
//    {

//        public unsafe bool TryParse(ref MsgHeader header, Stream source, Arena memoryAllocator, out OpQuery opQuery)
//        {
//            opQuery = default;

//            // Read flags
//            opQuery.Flags = (OpQuery.OperationFlags)Utils.ReadInt32FromStream(source);

//            // Read fullCollectionName (null-terminated string)
//            var stringBytes = Utils.ReadCStringFromStream(source, memoryAllocator);
//            opQuery.pFullCollectionName = (byte*)stringBytes
//                                            .ToIntPtr()
//                                            .ToPointer();

//            // Read numberToSkip
//            opQuery.NumberToSkip = Utils.ReadInt32FromStream(source);

//            // Read numberToReturn
//            opQuery.NumberToReturn = Utils.ReadInt32FromStream(source);

//            // Read query (assuming it's a BSON document represented by a byte array)
//            var queryDocument = Utils.ReadDocumentFromStream(source, memoryAllocator);
//            opQuery.pQuery = (byte*)queryDocument
//                                .ToIntPtr()
//                                .ToPointer();
//            opQuery.QueryLength = queryDocument.Length;

//            try
//            {
//                // Read returnFieldsSelector (assuming it's optional and is a BSON document represented by a byte array)
//                var returnFieldsSelectorDocument = Utils.ReadDocumentFromStream(source, memoryAllocator);
//                opQuery.pReturnFieldsSelector = (byte*)returnFieldsSelectorDocument
//                    .ToIntPtr()
//                    .ToPointer();

//                opQuery.ReturnFieldsSelectorLength = returnFieldsSelectorDocument.Length;
//            }
//            catch (IndexOutOfRangeException)
//            {
//                opQuery.pReturnFieldsSelector = null;
//                opQuery.ReturnFieldsSelectorLength = 0;
//                //this is an optional field, so don't care
//            }

//            return true; //TODO: make some robust error handling
//        }

//        /// <exception cref="SecurityException">The user does not have the required permission.</exception>
//        public unsafe Span<byte> GetRawBytes(OpQuery message, Arena memoryAllocator)
//        {
//            var size = message.FullCollectionNameLength +
//                       message.ReturnFieldsSelectorLength +
//                       message.QueryLength +
//                       sizeof(int) * 3;

//            var rawBytes = memoryAllocator.Allocate<byte>(size);
    
//            int offset = 0;

//            // Writing Flags
//            BitConverter.TryWriteBytes(rawBytes.Slice(offset, sizeof(int)), (int)message.Flags);
//            offset += sizeof(int);

//            // Writing FullCollectionName
//            new Span<byte>(message.pFullCollectionName, message.FullCollectionNameLength).CopyTo(rawBytes.Slice(offset, message.FullCollectionNameLength));
//            offset += message.FullCollectionNameLength;

//            // Writing NumberToSkip
//            BitConverter.TryWriteBytes(rawBytes.Slice(offset, sizeof(int)), message.NumberToSkip);
//            offset += sizeof(int);

//            // Writing NumberToReturn
//            BitConverter.TryWriteBytes(rawBytes.Slice(offset, sizeof(int)), message.NumberToReturn);
//            offset += sizeof(int);

//            // Writing Query
//            new Span<byte>(message.pQuery, message.QueryLength).CopyTo(rawBytes.Slice(offset, message.QueryLength));
//            offset += message.QueryLength;

//            // Writing ReturnFieldsSelector if applicable
//            if (message.ReturnFieldsSelectorLength > 0)
//            {
//                new Span<byte>(message.pReturnFieldsSelector, message.ReturnFieldsSelectorLength).CopyTo(rawBytes.Slice(offset, message.ReturnFieldsSelectorLength));
//            }
    
//            return rawBytes;
//        }

//    }
//}
