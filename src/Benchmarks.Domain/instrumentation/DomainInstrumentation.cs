using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NativeWeave.Server;

namespace NativeWeave.Reference.TaskService;

/// <summary>B23-only opt-in observers in an explicitly copied application, using standard listener APIs.</summary>
internal sealed class DomainInstrumentation : IDisposable
{
    private readonly string _mode;
    private readonly ActivityListener? _activities;
    private readonly MeterListener? _metrics;
    private readonly ConcurrentDictionary<string, double> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _peaks = new(StringComparer.Ordinal);
    private readonly object _metricsGate = new();
    private readonly Timer? _timer;
    private readonly ProbeLogger _logger;
    private bool _disposed;
    private long _sampled, _completed, _measurements, _observerFailures;
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    private DomainInstrumentation(NativeServerBuilder builder, TaskService service, string mode)
    {
        _mode = mode;
        bool tracing = mode is "debug-local" or "sampled" or "full" or "exporter-saturation" or "exporter-failure";
        bool metering = tracing || mode == "meter-only";
        bool logging = mode is "debug-local" or "sampled" or "full" or "log-only" or "exporter-saturation" or "exporter-failure";
        _logger = new(mode);
        if (tracing)
        {
            int interval = mode is "debug-local" or "sampled" ? 8 : 1;
            _activities = new()
            {
                ShouldListenTo = source => source.Name == "NativeWeave.Server",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => Interlocked.Increment(ref _sampled) % interval == 0
                    ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
                ActivityStopped = _ => Interlocked.Increment(ref _completed)
            };
            ActivitySource.AddActivityListener(_activities);
        }
        if (metering)
        {
            _metrics = new();
            _metrics.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "NativeWeave.Server") listener.EnableMeasurementEvents(instrument);
            };
            _metrics.SetMeasurementEventCallback<long>((instrument, value, _, _) => Observe(instrument.Name, value));
            _metrics.SetMeasurementEventCallback<double>((instrument, value, _, _) => Observe(instrument.Name, value));
            _metrics.Start();
            _timer = new(_ =>
            {
                try { lock (_metricsGate) { if (!_disposed) _metrics.RecordObservableInstruments(); } }
                catch { Interlocked.Increment(ref _observerFailures); }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        if (logging) builder.Services.AddLogging(loggingBuilder =>
            loggingBuilder.SetMinimumLevel(LogLevel.Warning).AddProvider(_logger));
        // The separate advanced cell exercises request-scope resolution. This
        // registration is shared by every instrumented profile, outside load.
        builder.Services.AddSingleton(service);
    }

    internal static DomainInstrumentation Configure(NativeServerBuilder builder, TaskService service)
    {
        string mode = Environment.GetEnvironmentVariable("B23_DIAGNOSTICS") ?? "release-disabled";
        if (mode is not ("release-disabled" or "debug-default" or "debug-local" or "sampled" or "full" or
            "meter-only" or "log-only" or "exporter-saturation" or "exporter-failure"))
            throw new ArgumentException("Unknown B23 instrumentation profile.");
        return new(builder, service, mode);
    }

    internal void Map(NativeServerApplication application, TaskService service)
    {
        application.MapGet("/__b23/observe", () => Results.Json(Snapshot(), DomainProbeJson.Default.DomainObservation))
            .RequireAuthorization("read").WithName("b23.observe");
        application.MapGet("/__b23/failure", (context, token) => FailureAsync(service, context, token))
            .RequireAuthorization("read").WithName("b23.failure");
        application.MapGet("/__b23/di-list", async (context, token) =>
        {
            int limit = context.Request.Query.Get<int>("limit");
            TaskService resolved = context.Services.GetRequiredService<TaskService>();
            return Results.Json(await resolved.ListAsync(Authentication.Identity(context), limit, null, token).ConfigureAwait(false), AppJson.Default.ItemPage);
        }).RequireAuthorization("read").WithName("b23.di-list");
    }

    private static async Task<ServerResult> FailureAsync(TaskService service, RequestContext context, CancellationToken token)
    {
        // This declared diagnostic profile executes the same typed Rust-backed
        // query before its controlled failure; it is separate from success cells.
        _ = await service.ListAsync(Authentication.Identity(context), 1, null, token).ConfigureAwait(false);
        throw new InvalidOperationException("B23 controlled diagnostic failure.");
    }

    private void Observe(string name, double value)
    {
        _values[name] = value;
        _peaks.AddOrUpdate(name, value, (_, prior) => Math.Max(prior, value));
        Interlocked.Increment(ref _measurements);
    }

    private DomainObservation Snapshot()
    {
        lock (_metricsGate) _metrics?.RecordObservableInstruments();
        using Process process = Process.GetCurrentProcess();
        return new(_mode, Configuration, GC.GetTotalAllocatedBytes(true), GC.GetTotalMemory(false),
            GC.GetGCMemoryInfo().TotalCommittedBytes, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            process.TotalProcessorTime.Ticks, process.WorkingSet64, Interlocked.Read(ref _completed),
            Interlocked.Read(ref _measurements), _logger.Calls, _logger.Exported, _logger.Failures,
            Interlocked.Read(ref _observerFailures),
            _metrics is null ? null : new Dictionary<string, double>(_values),
            _metrics is null ? null : new Dictionary<string, double>(_peaks));
    }

    public void Dispose()
    {
        _logger.Release();
        _timer?.Dispose();
        lock (_metricsGate)
        {
            _disposed = true;
            _metrics?.Dispose();
        }
        _activities?.Dispose();
    }

    private sealed class ProbeLogger(string mode) : ILoggerProvider, ILogger
    {
        private readonly ManualResetEventSlim _release = new();
        internal long Calls, Exported, Failures;
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref Calls);
            if (mode == "exporter-saturation") _release.Wait();
            if (mode == "exporter-failure")
            {
                Interlocked.Increment(ref Failures);
                throw new IOException("B23 controlled exporter failure.");
            }
            _ = formatter(state, error); // Configured sink pays actual formatting cost without collecting unbounded messages.
            Interlocked.Increment(ref Exported);
        }
        internal void Release() => _release.Set();
        public void Dispose() => Release();
    }
}

internal sealed record DomainObservation(string Mode, string Configuration, long AllocatedBytes,
    long HeapLiveBytes, long HeapCommittedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections,
    long CpuTimeSpanTicks, long WorkingSetBytes, long Activities, long Measurements,
    long LogCalls, long LogExports, long ExportFailures, long ObserverFailures,
    Dictionary<string, double>? Metrics, Dictionary<string, double>? MetricPeaks);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DomainObservation))]
internal sealed partial class DomainProbeJson : JsonSerializerContext;
