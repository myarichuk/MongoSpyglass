using Mongo.Fakes.Server;
var server = new MongoFakeServer(new BsonFileBackend(args[0]), port: int.Parse(args[1]));
await server.StartAsync();
Console.WriteLine($"READY {server.Port}");
await Task.Delay(Timeout.Infinite);
