using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using MongoDB.Bson;
using System.IO;
using System.Diagnostics;

class Program
{
    static async Task Main()
    {
        Console.WriteLine("Connecting to proxy at 127.0.0.1:27018...");
        try
        {
            using var client = new TcpClient("127.0.0.1", 27018);
            using var stream = client.GetStream();
            
            var doc = new BsonDocument
            {
                { "hello", 1 },
                { "client", new BsonDocument { { "driver", new BsonDocument { { "name", "benchmark" } } } } }
            };
            byte[] bson = doc.ToBson();
            
            int bodySize = 4 + 1 + bson.Length;
            int totalSize = 16 + bodySize;
            
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            writer.Write(totalSize);
            writer.Write(1); // RequestID
            writer.Write(0); // ResponseTo
            writer.Write(2013); // OpCode (OP_MSG)
            writer.Write(0); // Flags
            writer.Write((byte)0); // Kind 0
            writer.Write(bson);
            
            byte[] data = ms.ToArray();
            
            Console.WriteLine($"Starting burst of 1000 messages...");
            var sw = Stopwatch.StartNew();
            
            for (int i = 0; i < 1000; i++)
            {
                await stream.WriteAsync(data.AsMemory());
                // We don't read the response here, just flooding the proxy
            }
            
            Console.WriteLine($"Burst sent in {sw.ElapsedMilliseconds}ms. Waiting for proxy to process...");
            await Task.Delay(5000);
            Console.WriteLine("Done.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}
