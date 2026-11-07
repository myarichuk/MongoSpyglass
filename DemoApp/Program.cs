using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// The connection string points at the spyglass proxy, not at MongoDB (docker-compose.yml). appName is how the
// collector's UI attributes operations to a service.
builder.Services.AddSingleton<IMongoClient>(_ =>
    new MongoClient(builder.Configuration["Mongo:ConnectionString"] ?? "mongodb://localhost:27018/?appName=demoapp"));

builder.Services.AddControllers();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthorization();

app.MapControllers();

app.Run();
