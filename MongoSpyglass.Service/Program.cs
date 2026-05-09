using System.Net;
using Autofac.Extensions.DependencyInjection;
using Autofac;
using MongoSpyglass.Proxy;
using MongoSpyglass.Service.Data;
using MongoSpyglass.Service.Analyzers;
using Radzen;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    //.WriteTo.File("Logs/service-log.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddRadzenComponents();

builder.Services.AddSingleton<TrafficMonitorService>();
builder.Services.AddSingleton<ITrafficListener>(sp => sp.GetRequiredService<TrafficMonitorService>());

builder.Services.AddSingleton<RavenStorageService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<IProxySettingsProvider>(sp => sp.GetRequiredService<SettingsService>());
builder.Services.AddSingleton<NotificationHubService>();

builder.Services.AddSingleton<SlowQueryAnalyzer>();
builder.Services.AddSingleton<ITrafficListener>(sp => sp.GetRequiredService<SlowQueryAnalyzer>());
builder.Services.AddSingleton<IAnalyzerPlugin>(sp => sp.GetRequiredService<SlowQueryAnalyzer>());

builder.Services.AddSingleton<CursorAnalyzer>();
builder.Services.AddSingleton<ITrafficListener>(sp => sp.GetRequiredService<CursorAnalyzer>());
builder.Services.AddSingleton<IAnalyzerPlugin>(sp => sp.GetRequiredService<CursorAnalyzer>());

builder.Services.AddHostedService<MongoDbProxy>();

// Add any custom registrations here
// containerBuilder.RegisterType<YourType>().As<IYourInterface>();

builder.Host
    .UseSerilog()
    .UseServiceProviderFactory(new AutofacServiceProviderFactory());

var app = builder.Build();

// Initialize Services
var ravenService = app.Services.GetRequiredService<RavenStorageService>();
ravenService.Initialize();

var settingsService = app.Services.GetRequiredService<SettingsService>();
await settingsService.InitializeAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
