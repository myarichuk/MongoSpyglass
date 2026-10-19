using Spyglass.Collector;

// container HEALTHCHECK (the image has no shell or curl): spyglass-collector healthcheck [--bind host:port] [--path /healthz]
if (args is ["healthcheck", ..])
    return await HealthProbe.RunAsync(args[1..]);

CollectorOptions options;
try
{
    options = CollectorOptions.Load(args);
}
catch (ArgumentException e)
{
    Console.Error.WriteLine($"spyglass-collector: {e.Message}");
    return 2;
}

var ui = Path.Combine(AppContext.BaseDirectory, "wwwroot");
await using var server = await Server.StartAsync(options, Directory.Exists(ui) ? ui : null);
Console.WriteLine($"spyglass-collector {typeof(Server).Assembly.GetName().Version?.ToString(3)} listening on {server.BaseAddress} " +
                  $"(auth={options.Auth.ToString().ToLowerInvariant()}, data={options.Data ?? ":memory:"}, workers={options.Workers})");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.TrySetResult(); });
await stop.Task;
Console.WriteLine("spyglass-collector: shutting down");
return 0;
