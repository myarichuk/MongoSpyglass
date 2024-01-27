using MongoSpyglass.Proxy.WireProtocol.Raw.Parts;

namespace MongoSpyglass.Proxy.WireProtocol.Raw.Loaders;

internal class OpMsgLoader : OpMsgLoaderBase<Stream>
    {
        private readonly static AsyncLocal<byte> Kind = new();

        public static OpMsgLoader Instance { get; } = new();

        public override FlagBits LoadFlags(Stream source, GrowableArena allocator)
        {
            if (!source.TryReadEnum<FlagBits>(out var flags))
            {
                throw new InvalidOperationException("Unable to read flags");
            }

            return flags;
        }

        public override byte LoadKind(Stream source, GrowableArena allocator)
        {
            if (!source.TryRead<byte>(out var kind))
            {
                throw new InvalidOperationException("Unable to read section kind");
            }

            Kind.Value = kind;
            return kind;
        }

        public override Span<byte> LoadDataSection(Stream source, GrowableArena allocator)
        {
            switch(Kind.Value)
            {
                case 0:
                    return Kind0Loader.Instance.Load(source, allocator).BsonDocument;
                case 1:
                    if (!source.TryRead<int>(out var size))
                    {
                        throw new InvalidOperationException("Unable to read section size");
                    }                    

                    var sectionData = allocator.Allocate<byte>(size);

                    size.AsBytes(allocator).CopyTo(sectionData);
                    source.Read(sectionData[4..]);
                    
                    return sectionData;
                default:
                    throw new InvalidOperationException($"Unknown section kind: {Kind.Value}");
            }
        }

        internal class Kind0Loader : Kind0SectionLoaderBase<Stream>
        {
            public static Kind0Loader Instance { get; } = new();

            public override Span<byte> LoadBsonDocument(Stream source, GrowableArena allocator)
            {
                if (!source.TryReadBson(allocator, out var bsonAsBytes))
                {
                    throw new InvalidOperationException("Unable to read query");
                }

                return bsonAsBytes;            
            }
        }

        internal class Kind1Loader: Kind1SectionLoaderBase<Stream>
        {
            private readonly static AsyncLocal<int> Size = new();

            public static Kind1Loader Instance { get; } = new();

            public override int LoadSize(Stream source, GrowableArena allocator)
            {
                if (!source.TryRead<int>(out var fetchedValue))
                {
                    throw new InvalidOperationException("Unable to read section size");
                }

                Size.Value = fetchedValue;
                return fetchedValue;
            }

            public override Span<char> LoadDocumentSequenceIdentifier(Stream source, GrowableArena allocator)
            {
                if (!source.TryReadNativeStringFromStream(allocator, out var identifier))
                {
                    throw new InvalidOperationException("Unable to read document sequence identifier");
                }

                return identifier;
            }

            public override Span<byte> LoadBsonDocumentArray(Stream source, GrowableArena allocator)
            {
                var documentCollection = allocator.Allocate<byte>(Size.Value);
                var usedData = 0;

                while(source.TryReadBson(allocator, out var bsonAsBytes))
                {
                    bsonAsBytes.CopyTo(documentCollection[usedData..]);
                    usedData += bsonAsBytes.Length;
                }

                return documentCollection;
            }
        }
    }
