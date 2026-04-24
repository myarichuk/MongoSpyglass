using SharpArena.Allocators;
using MongoSpyglass.Proxy.WireProtocol;
using System;
using Xunit;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace MongoSpyglass.Proxy.Tests
{
    public class ArenaBsonReaderTests
    {
        private byte[] CreateSampleBson()
        {
            var doc = new BsonDocument
            {
                { "find", "my_collection" },
                { "$db", "my_db" },
                { "filter", new BsonDocument { { "x", 1 } } },
                { "batchSize", 100 }
            };
            return doc.ToBson();
        }

        [Fact]
        public void ArenaBsonReader_ZeroAllocationsOnParse()
        {
            byte[] bsonPayload = CreateSampleBson();

            // Pre-compute hashes to ensure no string allocation overhead from UTF8.GetBytes in the hot path
            uint findHash = ArenaBsonReader.ComputeHash("find");
            uint dbHash = ArenaBsonReader.ComputeHash("$db");
            uint filterHash = ArenaBsonReader.ComputeHash("filter");
            uint missingHash = ArenaBsonReader.ComputeHash("missing");

            // Warmup
            using var allocator = new ArenaAllocator();
            var reader = new ArenaBsonReader(bsonPayload, allocator);
            reader.TryFindElement(findHash, out _);
            reader.TryFindElement(dbHash, out _);
            reader.TryFindElement(filterHash, out _);

            allocator.Reset();

            long beforeAllocations = GC.GetAllocatedBytesForCurrentThread();

            var reader2 = new ArenaBsonReader(bsonPayload, allocator);

            bool foundFind = reader2.TryFindElement(findHash, out var findElement);
            bool foundDb = reader2.TryFindElement(dbHash, out var dbElement);
            bool foundFilter = reader2.TryFindElement(filterHash, out var filterElement);
            bool foundMissing = reader2.TryFindElement(missingHash, out _);

            // Fetch string spans to verify no allocations
            if (foundFind)
            {
                var span = reader2.GetStringSpan(findElement);
            }
            if (foundDb)
            {
                var span = reader2.GetStringSpan(dbElement);
            }

            long afterAllocations = GC.GetAllocatedBytesForCurrentThread();
            long allocations = afterAllocations - beforeAllocations;

            Assert.Equal(0, allocations);
        }

        [Fact]
        public void ArenaBsonReader_ParsesElementsCorrectly()
        {
            byte[] bsonPayload = CreateSampleBson();

            using var allocator = new ArenaAllocator();
            var reader = new ArenaBsonReader(bsonPayload, allocator);

            Assert.True(reader.TryFindElement("find", out var findElement));
            Assert.Equal(MongoSpyglass.Proxy.WireProtocol.BsonType.String, findElement.Type);
            Assert.Equal("my_collection", reader.GetStringValue(findElement));

            Assert.True(reader.TryFindElement("$db", out var dbElement));
            Assert.Equal(MongoSpyglass.Proxy.WireProtocol.BsonType.String, dbElement.Type);
            Assert.Equal("my_db", reader.GetStringValue(dbElement));

            Assert.True(reader.TryFindElement("filter", out var filterElement));
            Assert.Equal(MongoSpyglass.Proxy.WireProtocol.BsonType.Document, filterElement.Type);

            Assert.True(reader.TryFindElement("batchSize", out var batchSizeElement));
            Assert.Equal(MongoSpyglass.Proxy.WireProtocol.BsonType.Int32, batchSizeElement.Type);

            Assert.False(reader.TryFindElement("nonexistent", out _));
        }

        [Fact]
        public void ArenaBsonReader_WithStreamSimulation()
        {
            byte[] bsonPayload = CreateSampleBson();

            // Simulate a wire protocol message payload stream, OP_MSG body
            using var ms = new System.IO.MemoryStream(bsonPayload);
            var partialStream = new PartialReadStream(ms.ToArray(), 5);

            using var allocator = new ArenaAllocator();

            // Use existing StreamExtensions to pull it in
            bool success = partialStream.TryReadBson(allocator, out var readBson);
            Assert.True(success);

            var reader = new ArenaBsonReader(readBson, allocator);

            Assert.True(reader.TryFindElement("find", out var findElement));
            Assert.Equal("my_collection", reader.GetStringValue(findElement));
        }

        [Fact]
        public void ArenaBsonReader_ZeroAllocationsOnParse_WithStream()
        {
            byte[] bsonPayload = CreateSampleBson();

            using var ms = new System.IO.MemoryStream(bsonPayload);
            var streamBytes = ms.ToArray();

            uint findHash = ArenaBsonReader.ComputeHash("find");

            // Warmup
            using var allocator = new ArenaAllocator();
            var partialStream = new PartialReadStream(streamBytes, 5);
            partialStream.TryReadBson(allocator, out var readBson);
            var reader = new ArenaBsonReader(readBson, allocator);
            reader.TryFindElement(findHash, out _);

            allocator.Reset();

            var partialStream2 = new PartialReadStream(streamBytes, 5);

            long beforeAllocations = GC.GetAllocatedBytesForCurrentThread();

            bool success = partialStream2.TryReadBson(allocator, out var readBson2);

            var reader2 = new ArenaBsonReader(readBson2, allocator);
            bool foundFind = reader2.TryFindElement(findHash, out var findElement);

            if (foundFind)
            {
                var span = reader2.GetStringSpan(findElement);
            }

            long afterAllocations = GC.GetAllocatedBytesForCurrentThread();
            long allocations = afterAllocations - beforeAllocations;

            // Notice we only assert TryReadBson and parsing does not allocate.
            // PartialReadStream itself allocates on instantiation, so we created it BEFORE beforeAllocations.
            // TryReadBson allocates nothing because it puts everything in ArenaAllocator.
            Assert.Equal(0, allocations);
        }
    }
}
