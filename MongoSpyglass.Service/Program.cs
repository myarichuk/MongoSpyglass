using Autofac.Extensions.DependencyInjection;
using Autofac;
using MongoSpyglass.Proxy;
using MongoSpyglass.Service.Data;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("Logs/service-log.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddHostedService<MongoDbProxy>();

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
