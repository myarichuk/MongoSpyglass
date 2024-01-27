using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoSpyglass.Proxy;
using Serilog;
using System.Net;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();

var services = ConfigureServices();
var serviceProvider = services.BuildServiceProvider();
var logger = serviceProvider.GetRequiredService<ILogger<MongoDbProxy>>();

var mongoDbProxy = new MongoDbProxy(new IPEndPoint(IPAddress.Loopback, 27017), 27018, logger);

try
{
    await mongoDbProxy.StartAsync(CancellationToken.None);
}
finally
{
    await mongoDbProxy.StopAsync(CancellationToken.None);
}

static IServiceCollection ConfigureServices()
{
    IServiceCollection services = new ServiceCollection();

    services.AddLogging(configure => configure.AddSerilog());

    services.AddTransient<MongoDbProxy>();

    return services;
}

