using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NativeWeave.Server;

/// <summary>Copied redacted metadata only. No request, scope, native owner, key, URL or DTO survives into an exporter.</summary>
internal readonly record struct DiagnosticEvent(string Category, string Route, string Protocol, string StatusClass)
{
    internal int Bytes => checked(64 + Size(Category) + Size(Route) + Size(Protocol) + Size(StatusClass));
    private static int Size(string value) => Math.Max(checked(2 * value.Length), Encoding.UTF8.GetByteCount(value));
    internal LogLevel Level => Category is "application_failure" or "server_failure" or "native_failure" or "native_panic" or "poisoned" or "callback" or "contract" ? LogLevel.Error : LogLevel.Warning;
}
internal sealed class DiagnosticCounters
{
    internal static long TotalDropped;
    internal static long TotalRetainedBytes;
    internal static long TotalRetainedEntries;
    internal long Dropped;
    internal long ExportFailures;
    internal long RetainedBytes;
    internal long RetainedEntries;
    internal void Drop() { Interlocked.Increment(ref Dropped); Interlocked.Increment(ref TotalDropped); }
}
/// <summary>A finite metadata queue isolates synchronous ILogger and optional exporter stalls from business ownership.</summary>
internal sealed class BoundedDiagnostics
{
    private readonly Channel<DiagnosticEvent> _channel;
    private readonly Func<DiagnosticEvent, CancellationToken, ValueTask> _export;
    private readonly DiagnosticCounters _counters;
    private readonly object _gate = new();
    private readonly int _entries, _bytes, _itemBytes;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private bool _closed;
    internal BoundedDiagnostics(Func<DiagnosticEvent, CancellationToken, ValueTask> export, DiagnosticCounters counters, int entries = 2048, int bytes = 524_288, int itemBytes = 1024)
    {
        ArgumentNullException.ThrowIfNull(export); if (entries <= 0 || bytes <= 0 || itemBytes <= 0 || itemBytes > bytes) throw new ArgumentOutOfRangeException(nameof(entries));
        _export = export; _counters = counters; _entries = entries; _bytes = bytes; _itemBytes = itemBytes;
        _channel = Channel.CreateBounded<DiagnosticEvent>(new BoundedChannelOptions(entries) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, AllowSynchronousContinuations = false });
        // No business ExecutionContext, Activity, service scope or callback root is
        // captured by the background diagnostics worker.
        if (ExecutionContext.IsFlowSuppressed()) _worker = Task.Run(RunAsync);
        else { using (ExecutionContext.SuppressFlow()) _worker = Task.Run(RunAsync); }
    }
    internal bool TryWrite(in DiagnosticEvent value)
    {
        int size = value.Bytes;
        lock (_gate)
        {
            if (_closed || size > _itemBytes || _counters.RetainedEntries >= _entries || size > _bytes - _counters.RetainedBytes)
            { _counters.Drop(); return false; }
            Interlocked.Increment(ref _counters.RetainedEntries); Interlocked.Add(ref _counters.RetainedBytes, size);
            Interlocked.Increment(ref DiagnosticCounters.TotalRetainedEntries); Interlocked.Add(ref DiagnosticCounters.TotalRetainedBytes, size);
            if (_channel.Writer.TryWrite(value)) return true;
            Release(size); _counters.Drop(); return false;
        }
    }
    private void Release(int size)
    {
        Interlocked.Decrement(ref _counters.RetainedEntries); Interlocked.Add(ref _counters.RetainedBytes, -size);
        Interlocked.Decrement(ref DiagnosticCounters.TotalRetainedEntries); Interlocked.Add(ref DiagnosticCounters.TotalRetainedBytes, -size);
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var value in _channel.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                try { await _export(value, _stopping.Token).ConfigureAwait(false); }
                catch { Interlocked.Increment(ref _counters.ExportFailures); _counters.Drop(); }
                finally { Release(value.Bytes); }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        finally { while (_channel.Reader.TryRead(out var value)) { Release(value.Bytes); _counters.Drop(); } }
    }
    internal async Task StopAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate) { _closed = true; _channel.Writer.TryComplete(); }
        try { await _worker.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Cancellation registrations are exporter code too. Observe their
            // failures asynchronously; a blocked registration cannot extend the
            // independent flush deadline or retain a business owner.
            _ = ObserveCancellationAsync(_stopping.CancelAsync());
            // A stuck export owns only its bounded copied metadata. Never drop
            // its byte charge early or wait for it to release business resources.
            while (_channel.Reader.TryRead(out var value)) { Release(value.Bytes); _counters.Drop(); }
        }
    }
    private async Task ObserveCancellationAsync(Task cancellation)
    { try { await cancellation.ConfigureAwait(false); } catch { Interlocked.Increment(ref _counters.ExportFailures); _counters.Drop(); } }
    internal Task Completion => _worker;
    internal static BoundedDiagnostics ForLogger(ILogger logger, DiagnosticCounters counters)
    {
        const string format = "Server request failed: {Category}; route={Route}; protocol={Protocol}; status_class={StatusClass}.";
        var warning = LoggerMessage.Define<string, string, string, string>(LogLevel.Warning, new EventId(1001, "RequestFailure"), format);
        var error = LoggerMessage.Define<string, string, string, string>(LogLevel.Error, new EventId(1001, "RequestFailure"), format);
        return new((value, _) => { if (logger.IsEnabled(value.Level)) (value.Level == LogLevel.Error ? error : warning)(logger, value.Category, value.Route, value.Protocol, value.StatusClass, null); return ValueTask.CompletedTask; }, counters);
    }
}
