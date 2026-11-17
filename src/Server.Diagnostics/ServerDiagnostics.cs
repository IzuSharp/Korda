using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NativeWeave.Server.Native;
using NativeWeave.Interop;

namespace NativeWeave.Server;

/// <summary>Stable opt-in sources. Construction installs no listener, provider or exporter.</summary>
internal static class ServerDiagnostics
{
    internal const string SourceName = "NativeWeave.Server";
    internal static readonly string Version = typeof(NativeServerApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0-preview.1";
    internal static readonly ActivitySource Activities = new(SourceName, Version);
    internal static readonly Meter Meter = new(SourceName, Version);
    internal static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("nativeweave.server.request.duration", "s", "Admission to managed completion.");
    internal static readonly Histogram<double> RetirementDuration = Meter.CreateHistogram<double>("nativeweave.server.request.callback_retirement.duration", "s", "Admission to native request callback retirement; retained payload may outlive it.");
    private static readonly object Gate = new();
    private static readonly HashSet<ServerDiagnosticsController> Hosts = [];
    private static long _lastSample;
    private static DiagnosticSnapshot _cached;
    private static long _closedRejected, _closedNativeDrops, _closedCallbackTicks, _closedCallbackCount, _closedCallbackMax, _closedCallbackFailures;
    private static readonly ObservableGauge<double> Callback = Meter.CreateObservableGauge("nativeweave.server.callback.duration.mean", () => Snapshot().CallbackSeconds, "s");
    private static readonly ObservableGauge<double> CallbackMax = Meter.CreateObservableGauge("nativeweave.server.callback.duration.max", () => Snapshot().CallbackMaxSeconds, "s");
    private static readonly ObservableGauge<long>[] Counts =
    [
        Meter.CreateObservableGauge("nativeweave.server.requests.active", () => Snapshot().Active),
        Meter.CreateObservableGauge("nativeweave.server.requests.queued", () => Snapshot().Queued),
        Meter.CreateObservableGauge("nativeweave.server.requests.retiring", () => Snapshot().Retiring),
        Meter.CreateObservableGauge("nativeweave.server.payload.managed.bytes", () => Snapshot().Managed, "By"),
        Meter.CreateObservableGauge("nativeweave.server.payload.native.bytes", () => Snapshot().Native, "By"),
        Meter.CreateObservableGauge("nativeweave.server.diagnostics.queue.bytes", () => Interlocked.Read(ref DiagnosticCounters.TotalRetainedBytes), "By"),
        Meter.CreateObservableGauge("nativeweave.server.diagnostics.queue.entries", () => Interlocked.Read(ref DiagnosticCounters.TotalRetainedEntries))
    ];
    private static readonly ObservableCounter<long> Rejected = Meter.CreateObservableCounter("nativeweave.server.requests.rejected", () => Snapshot().Rejected);
    private static readonly ObservableCounter<long> CallbackFailures = Meter.CreateObservableCounter("nativeweave.server.callbacks.failed", () => Snapshot().CallbackFailures);
    private static readonly ObservableCounter<long> Drops = Meter.CreateObservableCounter("nativeweave.server.diagnostics.dropped", () => Snapshot().Dropped + Interlocked.Read(ref DiagnosticCounters.TotalDropped));
    internal static void Add(ServerDiagnosticsController host) { lock (Gate) { Hosts.Add(host); _lastSample = 0; } }
    internal static void Remove(ServerDiagnosticsController host)
    {
        lock (Gate)
        {
            if (!Hosts.Remove(host)) return;
            var snapshot = host.Snapshot();
            _closedRejected += snapshot.Rejected; _closedNativeDrops += snapshot.Dropped; _closedCallbackFailures += snapshot.CallbackFailures;
            _closedCallbackTicks += snapshot.CallbackTicks; _closedCallbackCount += snapshot.CallbackCount; _closedCallbackMax = Math.Max(_closedCallbackMax, snapshot.CallbackMax); _lastSample = 0;
        }
    }
    private static DiagnosticSnapshot Snapshot()
    {
        lock (Gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastSample != 0 && Stopwatch.GetElapsedTime(_lastSample, now) < TimeSpan.FromSeconds(1)) return _cached;
            var total = new DiagnosticSnapshot { Rejected = _closedRejected, Dropped = _closedNativeDrops, CallbackTicks = _closedCallbackTicks, CallbackCount = _closedCallbackCount, CallbackMax = _closedCallbackMax, CallbackFailures = _closedCallbackFailures };
            foreach (var host in Hosts) { Volatile.Write(ref host.SampleCallbacks, Callback.Enabled || CallbackMax.Enabled); total += host.Snapshot(); }
            _lastSample = now; _cached = total; return total;
        }
    }
    internal static string Category(Exception? error) => error switch
    {
        null => "none", OperationCanceledException => "canceled", TimeoutException => "timeout", RequestBindingException => "binding",
        ServerException { Code: "resource_limit" } => "resource_limit", ServerException { Code: "body_limit_exceeded" } => "body_limit",
        ServerException { Code: "transport_failed" } => "transport", ServerException { Code: "content_length_mismatch" } => "framing",
        ServerException { Code: "tls_configuration_failed" } => "tls", ServerException { Code: "listener_bind_failed" } => "listener",
        ServerException { Code: "unsupported_media_type" } => "media_type", ServerException => "server_failure",
        NativeEngineException { StatusCode: 8 } => "callback", NativeEngineException { StatusCode: 9 } => "native_panic",
        NativeEngineException { StatusCode: 10 } => "poisoned", NativeEngineException { StatusCode: 11 } => "contract", NativeEngineException => "native_failure", _ => "application_failure"
    };
    internal static string Bounded(string value)
    {
        int count = Math.Min(value.Length, 256);
        while (count > 0 && System.Text.Encoding.UTF8.GetByteCount(value.AsSpan(0, count)) > 256) count--;
        if (count > 0 && char.IsHighSurrogate(value[count - 1])) count--;
        return count == value.Length ? value : value[..count];
    }
}
internal struct DiagnosticSnapshot
{
    internal long Active, Queued, Retiring, Managed, Native, Rejected, Dropped, CallbackTicks, CallbackCount, CallbackMax, CallbackFailures;
    internal readonly double CallbackSeconds => CallbackCount == 0 ? 0 : (double)CallbackTicks / CallbackCount / Stopwatch.Frequency;
    internal readonly double CallbackMaxSeconds => (double)CallbackMax / Stopwatch.Frequency;
    public static DiagnosticSnapshot operator +(DiagnosticSnapshot a, DiagnosticSnapshot b) => new()
    {
        Active = a.Active + b.Active, Queued = a.Queued + b.Queued, Retiring = a.Retiring + b.Retiring,
        Managed = a.Managed + b.Managed, Native = a.Native + b.Native, Rejected = a.Rejected + b.Rejected,
        Dropped = a.Dropped + b.Dropped, CallbackTicks = a.CallbackTicks + b.CallbackTicks, CallbackCount = a.CallbackCount + b.CallbackCount, CallbackMax = Math.Max(a.CallbackMax, b.CallbackMax), CallbackFailures = a.CallbackFailures + b.CallbackFailures
    };
}
internal sealed class ServerDiagnosticsController
{
    private NativeHost? _host;
    private readonly ILogger? _logger;
    private BoundedDiagnostics? _queue;
    private readonly object _gate = new();
    private readonly DiagnosticCounters _counters = new();
    private NwServerSnapshotV1 _final;
    private bool _frozen;
    internal bool SampleCallbacks;
    private long _callbackTicks, _callbackCount, _callbackMax;
    internal ServerDiagnosticsController(ServerHost host, IServiceProvider services)
    {
        _host = host as NativeHost;
        try { _logger = services.GetService<ILoggerFactory>()?.CreateLogger(ServerDiagnostics.SourceName); } catch { _counters.Drop(); }
        if (_host is not null) _host.Diagnostics = this;
        ServerDiagnostics.Add(this);
    }
    internal ServerRequestDiagnostics? Start(RequestContext context)
    {
        try
        {
            if (!ServerDiagnostics.Activities.HasListeners() && !ServerDiagnostics.RequestDuration.Enabled && !ServerDiagnostics.RetirementDuration.Enabled &&
                _logger?.IsEnabled(LogLevel.Warning) != true && _logger?.IsEnabled(LogLevel.Error) != true) return null;
            return new(this, context);
        }
        catch { _counters.Drop(); return null; }
    }
    internal void RecordCallback(long start)
    {
        if (start == 0) return;
        long elapsed = Stopwatch.GetTimestamp() - start;
        Interlocked.Add(ref _callbackTicks, elapsed); Interlocked.Increment(ref _callbackCount);
        long prior = Volatile.Read(ref _callbackMax);
        while (elapsed > prior) { long observed = Interlocked.CompareExchange(ref _callbackMax, elapsed, prior); if (observed == prior) break; prior = observed; }
    }
    internal void Freeze(in NwServerSnapshotV1 snapshot) { lock (_gate) { _final = snapshot; _frozen = true; } }
    internal DiagnosticSnapshot Snapshot()
    {
        NwServerSnapshotV1 value;
        lock (_gate)
        {
            value = _final;
            if (!_frozen && _host is { } host)
                try { value = host.Snapshot(); _final = value; } catch { _counters.Drop(); }
        }
        return new()
        {
            Active = (long)value.active_requests, Queued = (long)value.queued_requests, Retiring = (long)value.retiring_requests,
            Managed = (long)value.retained_managed_bytes, Native = (long)value.retained_native_bytes, Rejected = (long)value.rejected_requests,
            Dropped = (long)value.diagnostic_drops, CallbackFailures = (long)value.callback_failures, CallbackTicks = Interlocked.Read(ref _callbackTicks), CallbackCount = Interlocked.Read(ref _callbackCount), CallbackMax = Interlocked.Read(ref _callbackMax)
        };
    }
    internal void Dropped() => _counters.Drop();
    internal void Failure(in DiagnosticEvent value)
    {
        if (_logger?.IsEnabled(value.Level) != true) return;
        lock (_gate) { if (_host is null) return; (_queue ??= BoundedDiagnostics.ForLogger(_logger, _counters)).TryWrite(value); }
    }
    internal async Task StopAsync()
    {
        BoundedDiagnostics? queue; lock (_gate) { _host = null; queue = _queue; }
        try { if (queue is not null) await queue.StopAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false); }
        catch { _counters.Drop(); }
        finally { ServerDiagnostics.Remove(this); }
    }
}
/// <summary>Only the awaited managed dispatch scope owns its Activity. No Activity crosses the ABI.</summary>
internal sealed class ServerRequestDiagnostics
{
    private readonly ServerDiagnosticsController _owner;
    private readonly Activity? _activity, _previousActivity;
    private readonly NativeRequest? _native;
    private readonly string _route, _protocol;
    private readonly long _started;
    private readonly double _admittedOffset;
    private double _managedSeconds;
    private string _statusClass = "2xx";
    internal ServerRequestDiagnostics(ServerDiagnosticsController owner, RequestContext context)
    {
        _owner = owner; _route = context.Route.Pattern; _protocol = context.Native.Protocol; _native = context.Native as NativeRequest;
        _admittedOffset = Math.Max(0, context.Route.EffectiveTimeout.TotalSeconds - context.Native.TimeRemaining.TotalSeconds);
        _started = Stopwatch.GetTimestamp(); _previousActivity = Activity.Current;
        if (!ServerDiagnostics.Activities.HasListeners()) return;
        try
        {
            ActivityContext parent = context.Native is NativeRequest native ? native.DiagnosticParent : default;
            // An unrelated ThreadPool Activity is never an implicit request parent.
            // The previous managed scope is restored after callback retirement.
            Activity.Current = null;
            _activity = ServerDiagnostics.Activities.StartActivity("request", ActivityKind.Server, parent);
            if (_activity is null) Activity.Current = _previousActivity;
            if (_activity is not null && context.Native is NativeRequest tracked) tracked.UseDiagnosticTraceId(_activity.TraceId);
            if (_activity?.IsAllDataRequested == true)
            { _activity.SetTag("http.route", ServerDiagnostics.Bounded(_route)); _activity.SetTag("network.protocol.version", _protocol); }
        }
        catch { Activity.Current = _previousActivity; throw; }
    }
    internal void ManagedCompleted(int status)
    {
        try
        {
            _managedSeconds = _admittedOffset + Stopwatch.GetElapsedTime(_started).TotalSeconds;
            _statusClass = (status / 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "xx";
        }
        catch { _owner.Dropped(); }
    }
    internal void Complete(Exception? failure)
    {
        try
        {
            string category = failure is OperationCanceledException && _native?.DiagnosticCancellation == 12 ? "timeout" : ServerDiagnostics.Category(failure);
            if (_activity?.IsAllDataRequested == true)
            { _activity.SetTag("http.response.status_class", _statusClass); if (failure is not null) _activity.SetStatus(ActivityStatusCode.Error, category); }
            if (ServerDiagnostics.RequestDuration.Enabled || ServerDiagnostics.RetirementDuration.Enabled)
            {
                TagList tags = new() { { "http.route", ServerDiagnostics.Bounded(_route) }, { "network.protocol.version", _protocol }, { "http.response.status_class", _statusClass }, { "error.type", category } };
                if (ServerDiagnostics.RequestDuration.Enabled) ServerDiagnostics.RequestDuration.Record(_managedSeconds, tags);
                if (ServerDiagnostics.RetirementDuration.Enabled) ServerDiagnostics.RetirementDuration.Record(_admittedOffset + Stopwatch.GetElapsedTime(_started).TotalSeconds, tags);
            }
            if (failure is not null) _owner.Failure(new(category, ServerDiagnostics.Bounded(_route), _protocol, _statusClass));
        }
        catch { _owner.Dropped(); }
        finally
        {
            try { _activity?.Dispose(); } catch { _owner.Dropped(); }
            finally { try { if (_activity is not null) Activity.Current = _previousActivity; } catch { _owner.Dropped(); } }
        }
    }
}
