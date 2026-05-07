using System.Net;
using Autofac.Extensions.DependencyInjection;
using Autofac;
using MongoSpyglass.Proxy;
using MongoSpyglass.Service.Data;
using Radzen;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("Logs/service-log.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddRadzenComponents();

builder.Services.AddSingleton<TrafficMonitorService>();
builder.Services.AddSingleton<ITrafficListener>(sp => sp.GetRequiredService<TrafficMonitorService>());

builder.Services.AddHostedService(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<MongoDbProxy>>();
    var listeners = sp.GetServices<ITrafficListener>();
    
    var serverAddr = IPAddress.Parse(config["Proxy:MongoDbServer"] ?? "127.0.0.1");
    var serverPort = int.Parse(config["Proxy:MongoDbPort"] ?? "27017");
    var incomingPort = int.Parse(config["Proxy:IncomingPort"] ?? "27018");

    return new MongoDbProxy(new IPEndPoint(serverAddr, serverPort), incomingPort, logger, listeners);
});

// Create a container builder
var containerBuilder = new ContainerBuilder();

// Add any custom registrations here
// containerBuilder.RegisterType<YourType>().As<IYourInterface>();

builder.Host
    .UseSerilog()
    .UseServiceProviderFactory(new AutofacServiceProviderFactory());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
