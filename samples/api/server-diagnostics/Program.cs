using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NativeWeave.Server;

bool local = args.Contains("--local", StringComparer.Ordinal);
int activities = 0, measurements = 0, sample = 0;
using ActivityListener? tracing = local ? new()
{
    ShouldListenTo = source => source.Name == "NativeWeave.Server",
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ++sample % 8 == 0 ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
    ActivityStopped = _ => Interlocked.Increment(ref activities)
} : null;
if (tracing is not null) ActivitySource.AddActivityListener(tracing);
using MeterListener? metrics = local ? new() : null;
if (metrics is not null)
{
    metrics.InstrumentPublished = (instrument, listener) => { if (instrument.Meter.Name == "NativeWeave.Server") listener.EnableMeasurementEvents(instrument); };
    metrics.SetMeasurementEventCallback<long>((_, _, _, _) => Interlocked.Increment(ref measurements));
    metrics.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref measurements));
    metrics.Start();
}
var warnings = new LocalWarnings();
var builder = NativeServer.CreateBuilder().Listen(IPAddress.Loopback, 0);
if (local) builder.Services.AddLogging(logging => { logging.SetMinimumLevel(LogLevel.Warning); logging.AddProvider(warnings); });
await using var app = builder.Build();
app.MapGet("/hello/{name}", static () => Results.Text("Hello from the Rust Host."));
app.MapGet("/failure", static () => throw new InvalidOperationException("Application details stay out of diagnostic output."));
await app.StartAsync();
using var client = new HttpClient { BaseAddress = app.BoundAddresses.Single(), Timeout = TimeSpan.FromSeconds(5) };
for (int index = 0; index < 16; index++)
{
    using var response = await client.GetAsync(index % 4 == 0 ? "failure" : "hello/ordinary-dto");
    if ((int)response.StatusCode != (index % 4 == 0 ? 500 : 200)) throw new InvalidOperationException("Unexpected business result.");
}
var close = await app.CloseAsync(TimeSpan.FromSeconds(2));
if (close.Status != ServerCloseStatus.Completed || close.PendingRequests != 0 || close.RetainedNativeBytes != 0 || close.RetainedManagedBytes != 0) throw new InvalidOperationException("Ownership did not join.");
await app.DisposeAsync(); metrics?.RecordObservableInstruments();
if (!local && (activities != 0 || measurements != 0)) throw new InvalidOperationException("Diagnostics activated implicitly.");
if (local && (activities != 2 || measurements == 0 || warnings.Count != 4)) throw new InvalidOperationException("Explicit diagnostic listener did not run.");
Console.WriteLine($"Rust Host complete; explicit_local={local}; activities={activities}; measurements={measurements}.");

sealed class LocalWarnings : ILoggerProvider, ILogger
{
    private int _remaining = 32;
    internal int Count => 32 - Volatile.Read(ref _remaining);
    public ILogger CreateLogger(string categoryName) => this;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning && Volatile.Read(ref _remaining) > 0;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
    {
        if (Interlocked.Decrement(ref _remaining) < 0) return;
        string text = format(state, error);
        Console.WriteLine(text.Length <= 256 ? text : text[..256]);
    }
    public void Dispose() { }
}
