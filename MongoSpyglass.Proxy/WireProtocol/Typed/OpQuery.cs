using MongoDB.Bson;

namespace MongoSpyglass.Proxy.WireProtocol.Typed
{
    public record struct OpQuery
    {
        public OperationFlags Flags { get; init; }

        public string FullCollectionName { get; init; }

        public int NumberToSkip { get; init; }
        public int NumberToReturn { get; init; }

        public BsonDocument Query { get; init; }
        public BsonDocument ReturnFieldsSelector { get; init; }

        public static OpQuery FromRaw(Raw.OpQuery opQuery)
        {
            return new OpQuery
            {
                Flags = opQuery.Flags,
                FullCollectionName = opQuery.FullCollectionName.AsString(),
                NumberToSkip = opQuery.NumberToSkip,
                NumberToReturn = opQuery.NumberToReturn,
                Query = opQuery.Query.AsBson(),
                ReturnFieldsSelector = opQuery.ReturnFieldsSelector.AsBson()
            };
        }
    }
}
