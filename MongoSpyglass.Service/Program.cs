using System.Net;
using Autofac.Extensions.DependencyInjection;
using Autofac;
using MongoSpyglass.Proxy;
using MongoSpyglass.Proxy.Profiling;
using MongoSpyglass.Service.Data;
using MongoSpyglass.Service.Analyzers;
using OpenTelemetry.Metrics;
using Radzen;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    //.WriteTo.File("Logs/service-log.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Check if headless mode is enabled (from config/env) before registering UI services
bool headlessMode = builder.Configuration.GetValue<bool>("Blazor:HeadlessMode") ||
                    (bool.TryParse(Environment.GetEnvironmentVariable("HEADLESS_MODE"), out var envHeadless) && envHeadless);

if (!headlessMode)
{
    builder.Services.AddRazorPages();
    builder.Services.AddServerSideBlazor();
    builder.Services.AddRadzenComponents();
}

builder.Services.AddSingleton<TrafficMonitorService>();
builder.Services.AddSingleton<ITrafficListener>(sp => sp.GetRequiredService<TrafficMonitorService>());

builder.Services.AddSingleton<RavenStorageService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<IProxySettingsProvider>(sp => sp.GetRequiredService<SettingsService>());
builder.Services.AddSingleton<NotificationHubService>();

builder.Services.AddSingleton<ReteAnalyzerEngine>();
builder.Services.AddSingleton<IAnalyzerPlugin>(sp => sp.GetRequiredService<ReteAnalyzerEngine>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReteAnalyzerEngine>());
builder.Services.AddSingleton<ITrafficListener>(sp => sp.GetRequiredService<ReteAnalyzerEngine>());

builder.Services.AddSingleton<CorrelationRingBuffer>();
builder.Services.AddSingleton<MetricsService>();

builder.Services.AddOpenTelemetry()
    .WithMetrics(meterBuilder =>
    {
        meterBuilder
            .AddPrometheusExporter()
            .AddMeter("MongoSpyglass");
    });

builder.Services.AddHostedService<MongoDbProxy>();

// Add any custom registrations here
// containerBuilder.RegisterType<YourType>().As<IYourInterface>();

builder.Host
    .UseSerilog()
    .UseServiceProviderFactory(new AutofacServiceProviderFactory());

var app = builder.Build();

// Initialize Services
var ravenService = app.Services.GetRequiredService<RavenStorageService>();
var config = app.Services.GetRequiredService<IConfiguration>();
ravenService.Initialize(dataDir: config["RavenDB:DataDir"]);

var settingsService = app.Services.GetRequiredService<SettingsService>();
await settingsService.InitializeAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();

app.UseRouting();

// Prometheus metrics endpoint
app.UseOpenTelemetryPrometheusScrapingEndpoint();

if (!headlessMode)
{
    app.MapBlazorHub();
    app.MapFallbackToPage("/_Host");
}

app.Run();
