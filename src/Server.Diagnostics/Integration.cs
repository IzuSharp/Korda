using System.Diagnostics;
namespace NativeWeave.Server;

public sealed partial class NativeServerApplication
{
    private ServerDiagnosticsController? _diagnostics;
    private void InitializeDiagnostics()
    { try { _diagnostics = new(_host, Services); } catch { Interlocked.Increment(ref DiagnosticCounters.TotalDropped); } }
    private ServerRequestDiagnostics? BeginDiagnostics(RequestContext context) => _diagnostics?.Start(context);
    private Task FlushDiagnosticsAsync() => _diagnostics?.StopAsync() ?? Task.CompletedTask;
}
internal sealed partial class NativeHost
{
    internal ServerDiagnosticsController? Diagnostics;
    private long BeginCallbackDiagnostics() => Diagnostics is { } diagnostics && Volatile.Read(ref diagnostics.SampleCallbacks) ? Stopwatch.GetTimestamp() : 0;
    private void RecordCallbackDiagnostics(long started) => Diagnostics?.RecordCallback(started);
    private void FreezeDiagnostics()
    { if (Diagnostics is { } diagnostics) try { diagnostics.Freeze(Snapshot()); } catch { diagnostics.Dropped(); } }
}
