using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NativeWeave.Server;

internal static class Costs
{
    internal static async Task RunAsync(string mode)
    {
        if (mode is not ("disabled" or "sampled" or "full" or "exporter-fault")) throw new ArgumentException("Choose disabled, sampled, full or exporter-fault.", nameof(mode));
        using var traces = mode == "disabled" ? null : new TraceCollector(ActivitySamplingResult.AllDataAndRecorded, mode == "sampled" ? 8 : 1);
        using var metrics = mode == "disabled" ? null : new MetricCollector();
        var logger = new CaptureLogger { Throw = mode == "exporter-fault" };
        var builder = NativeServer.CreateBuilder().Listen(IPAddress.Loopback, 0);
        if (mode != "disabled") builder.Services.AddLogging(logging => { logging.SetMinimumLevel(LogLevel.Warning); logging.AddProvider(logger); });
        await using var app = builder.Build(); app.MapGet("/matched/{id}", static () => throw new InvalidOperationException("REDACTED"));
        await app.StartAsync(); using var client = Program.Client(app); metrics?.Observe();
        const int requests = 300; var latency = new double[requests];
        for (int i = 0; i < 10; i++) { using var warm = await client.GetAsync("matched/warm"); if ((int)warm.StatusCode != 500) throw new InvalidOperationException("Warm-up result differs."); }
        using var process = Process.GetCurrentProcess();
        long allocated = GC.GetTotalAllocatedBytes(true); TimeSpan cpu = process.TotalProcessorTime; var elapsed = Stopwatch.StartNew();
        long queueBytes = 0, queueEntries = 0;
        for (int i = 0; i < requests; i++)
        {
            var requestClock = Stopwatch.GetTimestamp(); using var response = await client.GetAsync("matched/value");
            if ((int)response.StatusCode != 500 || (await response.Content.ReadAsByteArrayAsync()).Length != 0) throw new InvalidOperationException("Matched workload differs.");
            latency[i] = Stopwatch.GetElapsedTime(requestClock).TotalMilliseconds;
            queueBytes = Math.Max(queueBytes, Interlocked.Read(ref DiagnosticCounters.TotalRetainedBytes));
            queueEntries = Math.Max(queueEntries, Interlocked.Read(ref DiagnosticCounters.TotalRetainedEntries));
        }
        elapsed.Stop(); double cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds; allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        await Task.Delay(1050); metrics?.Observe();
        var close = await app.CloseAsync(TimeSpan.FromSeconds(3));
        if (close.Status != ServerCloseStatus.Completed || close.PendingRequests != 0 || close.RetainedManagedBytes != 0 || close.RetainedNativeBytes != 0) throw new InvalidOperationException("Diagnostics changed retirement.");
        await app.DisposeAsync(); Array.Sort(latency); process.Refresh();
        string callback = metrics is null ? "unobserved" : metrics.Latest("nativeweave.server.callback.duration.max").ToString("R", CultureInfo.InvariantCulture);
        Console.WriteLine("mode,configuration,dynamic,requests,elapsed_ms,cpu_ms,process_allocated_bytes,p50_ms,p95_ms,working_set_bytes,queue_peak_bytes,queue_peak_entries,dropped,callback_max_seconds,activities");
        Console.WriteLine(FormattableString.Invariant($"{mode},{Program.Configuration},{RuntimeFeature.IsDynamicCodeSupported},{requests},{elapsed.Elapsed.TotalMilliseconds:F3},{cpuMilliseconds:F3},{allocated},{latency[requests / 2]:F3},{latency[(int)(requests * .95)]:F3},{process.WorkingSet64},{queueBytes},{queueEntries},{Interlocked.Read(ref DiagnosticCounters.TotalDropped)},{callback},{traces?.Completed.Count ?? 0}"));
    }
}
