using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using NativeWeave.Data;

namespace NativeWeave.Verification.EndToEnd;

/// <summary>Explicit test-consumer telemetry using public runtime/package interfaces only.</summary>
/// <remarks>Linked into an independently identified B21-source consumer. No framework internal
/// hook, friend assembly, reflection or private native host handle is used. These observations
/// do not make unobserved managed roots or process-native allocated bytes equal to zero.</remarks>
internal static class ReferenceObserver
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Gauges = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> Errors = new(StringComparer.Ordinal);
    private static readonly List<NativeCounts> Native = [];
    private static FileStream? _output;
    private static MeterListener? _listener;
    private static Timer? _timer;
    private static long _sequence;
    private static long _failures;
    private static long _errorOverflow;
    private static int _sampling;
    private static long _skippedOverlaps;
    private static bool _stopping;
    private static string? _controlPath;
    private static long _controlSequence;
    [ThreadStatic] private static bool _reportingError;

    [ModuleInitializer]
    internal static void Initialize()
    {
        string? destination = Environment.GetEnvironmentVariable("NW_B22_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(destination)) return;
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("B22 observation path must be absolute.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        _output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.None);
        _controlPath = Environment.GetEnvironmentVariable("NW_B22_OBSERVER_CONTROL");
        if (_controlPath is not null && !Path.IsPathFullyQualified(_controlPath))
            throw new ArgumentException("B22 observer control path must be absolute.");
        string? native = Environment.GetEnvironmentVariable("NW_B22_DB_MODULES");
        if (native is not null)
        {
            if (native.Length > 16_384) throw new ArgumentException("B22 native selection is bounded.");
            using var document = JsonDocument.Parse(native);
            foreach (var value in document.RootElement.EnumerateObject())
            {
                if (value.Name is not ("sqlite" or "postgresql") || Native.Count >= 2)
                    throw new ArgumentException("B22 native provider selection is invalid.");
                string path = value.Value.GetString() ?? throw new ArgumentException("B22 native path is required.");
                if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("B22 native module path must be explicit.");
                Native.Add(new(value.Name, path));
            }
        }
        _listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == "NativeWeave.Server") listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(static (instrument, value, _, _) =>
        {
            lock (Gate)
            {
                // The observed public source has a finite fixed inventory.
                if (Gauges.Count < 32 || Gauges.ContainsKey(instrument.Name)) Gauges[instrument.Name] = value;
                else Interlocked.Increment(ref _failures);
            }
        });
        _listener.Start();
        AppDomain.CurrentDomain.FirstChanceException += ObserveException;
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Stop();
        Sample("observer-start");
        _timer = new Timer(static _ => Tick(), null, TimeSpan.FromMilliseconds(1100), TimeSpan.FromMilliseconds(1100));
        if (Environment.GetEnvironmentVariable("NW_B22_HANDLE_CONTROL") == "1") HandleControl();
    }

    private static void Tick()
    {
        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
        { Interlocked.Increment(ref _skippedOverlaps); return; }
        try
        {
            if (_controlPath is not null && File.Exists(_controlPath))
            {
                using var stream = new FileStream(_controlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 512) throw new ArgumentException("Observer control exceeds its bound.");
                using var document = JsonDocument.Parse(stream);
                long sequence = document.RootElement.GetProperty("sequence").GetInt64();
                if (sequence > _controlSequence)
                {
                    if (!document.RootElement.GetProperty("collect").GetBoolean())
                        throw new ArgumentException("Only explicit diagnostic collection is supported.");
                    _controlSequence = sequence;
                    SampleCore("idle-before-finalizers");
                    ControlEvents.Log.Observation(sequence, "idle-before-finalizers");
                    CollectDiagnostic();
                    SampleCore("idle-after-finalizers");
                    ControlEvents.Log.Observation(sequence, "idle-after-finalizers");
                }
            }
            SampleCore("sample");
        }
        catch { Interlocked.Increment(ref _failures); }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    private static void ObserveException(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (_reportingError || args.Exception is not DatabaseException error) return;
        _reportingError = true;
        try
        {
            string code = error.ProviderCode ?? "none";
            if (code.Length > 32 || code.Any(value => !char.IsAsciiLetterOrDigit(value) && value != '_')) code = "redacted";
            string key = error.Kind + ":" + code;
            lock (Gate)
            {
                if (Errors.TryGetValue(key, out long count)) Errors[key] = checked(count + 1);
                else if (Errors.Count < 32) Errors.Add(key, 1);
                else _errorOverflow++;
            }
        }
        finally { _reportingError = false; }
    }

    private static void Sample(string phase)
    {
        if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
        { Interlocked.Increment(ref _skippedOverlaps); return; }
        try { SampleCore(phase); }
        finally { Volatile.Write(ref _sampling, 0); }
    }

    private static void SampleCore(string phase)
    {
        // Never hold the observer lock while calling a package or native module.
        // A FirstChanceException callback can arrive while those components own
        // their own locks, so imposing the reverse lock order would change liveness.
        if (Volatile.Read(ref _stopping) && phase is not ("observer-stop" or "observer-before-finalizers")) return;
        List<(string Provider, ulong[] Counts)> native;
        GCMemoryInfo gc;
        try
        {
            _listener!.RecordObservableInstruments();
            native = Native.Select(value => (value.Provider, value.Read())).ToList();
            gc = GC.GetGCMemoryInfo();
        }
        catch { Interlocked.Increment(ref _failures); return; }
        lock (Gate)
        {
            if (_output is null || _stopping && phase is not ("observer-stop" or "observer-before-finalizers")) return;
            try
            {
                using var memory = new MemoryStream(4096);
                using (var writer = new Utf8JsonWriter(memory))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", 1); writer.WriteNumber("sequence", ++_sequence);
                    writer.WriteNumber("controlSequence", _controlSequence);
                    writer.WriteString("phase", phase); writer.WriteNumber("timestamp", Stopwatch.GetTimestamp());
                    writer.WriteNumber("timestampFrequency", Stopwatch.Frequency);
                    writer.WriteNumber("unixMilliseconds", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    writer.WriteBoolean("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
                    writer.WriteStartObject("serverMeter");
                    foreach (var value in Gauges) writer.WriteNumber(value.Key, value.Value);
                    writer.WriteEndObject();
                    writer.WriteBoolean("serverMeterObserved", Gauges.Count != 0);
                    writer.WriteStartObject("databaseNativeOwners");
                    foreach (var value in native)
                    {
                        writer.WriteStartArray(value.Provider);
                        foreach (ulong count in value.Counts) writer.WriteNumberValue(count);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                    writer.WriteStartObject("managed");
                    writer.WriteNumber("totalAllocatedBytes", GC.GetTotalAllocatedBytes(false));
                    writer.WriteNumber("lastGcHeapBytes", gc.HeapSizeBytes);
                    writer.WriteNumber("lastGcCommittedBytes", gc.TotalCommittedBytes);
                    writer.WriteNumber("lastGcPinnedObjects", gc.PinnedObjectsCount);
                    writer.WriteNumber("gen0Collections", GC.CollectionCount(0));
                    writer.WriteNumber("gen1Collections", GC.CollectionCount(1));
                    writer.WriteNumber("gen2Collections", GC.CollectionCount(2));
                    writer.WriteNull("liveNormalGcHandleRoots");
                    writer.WriteString("rootObservation", "external EventPipe required; not inferred from pins, RSS or native owners");
                    writer.WriteEndObject();
                    writer.WriteStartObject("databaseFirstChanceCodes");
                    foreach (var value in Errors) writer.WriteNumber(value.Key, value.Value);
                    writer.WriteEndObject();
                    writer.WriteNumber("errorCodeOverflow", _errorOverflow);
                    writer.WriteNumber("observerFailures", Interlocked.Read(ref _failures));
                    writer.WriteNumber("skippedOverlaps", Interlocked.Read(ref _skippedOverlaps));
                    writer.WriteEndObject();
                }
                if (memory.Length > 32_768) throw new InvalidOperationException("Observer record exceeded its fixed bound.");
                memory.Position = 0; memory.CopyTo(_output); _output.WriteByte((byte)'\n'); _output.Flush();
            }
            catch { Interlocked.Increment(ref _failures); }
        }
    }

    private static void HandleControl()
    {
        // These are known test roots only. They are not a framework owner count.
        const int normalCount = 8, pinnedCount = 4;
        var handles = new GCHandle[normalCount + pinnedCount];
        var identities = new string[handles.Length];
        ControlEvents.Log.Phase("begin");
        try
        {
            for (int index = 0; index < handles.Length; index++)
            {
                handles[index] = GCHandle.Alloc(new byte[257 + index], index < normalCount ? GCHandleType.Normal : GCHandleType.Pinned);
                identities[index] = unchecked((ulong)GCHandle.ToIntPtr(handles[index]).ToInt64()).ToString("x");
            }
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            WriteControl("allocated", identities, normalCount, pinnedCount);
            ControlEvents.Log.Phase("allocated");
        }
        finally
        {
            foreach (var handle in handles) if (handle.IsAllocated) handle.Free();
        }
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        ControlEvents.Log.Phase("end");
        WriteControl("freed", identities, normalCount, pinnedCount);
    }

    private static void WriteControl(string phase, string[] identities, int normal, int pinned)
    {
        string path = Environment.GetEnvironmentVariable("NW_B22_HANDLE_CONTROL_OUTPUT")
            ?? throw new ArgumentException("Handle controls require an explicit expectation file.");
        using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteString("phase", phase);
            writer.WriteNumber("normal", normal); writer.WriteNumber("pinned", pinned);
            writer.WriteStartArray("handles"); foreach (string value in identities) writer.WriteStringValue(value); writer.WriteEndArray();
            writer.WriteEndObject();
        }
        output.WriteByte((byte)'\n'); output.Flush(true);
    }

    private static void Stop()
    {
        lock (Gate) { if (_stopping) return; _stopping = true; }
        _timer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Sample("observer-before-finalizers");
        // Keep pre-finalization values. This explicit diagnostic GC distinguishes
        // unreachable closed SafeHandles from still-rooted/live owners; it is not
        // part of the production application or a claimed latency measurement.
        CollectAndAwaitFinalizers();
        Sample("observer-stop");
        AppDomain.CurrentDomain.FirstChanceException -= ObserveException;
        _listener?.Dispose();
        lock (Gate)
        {
            _output?.Flush(true); _output?.Dispose(); _output = null;
            foreach (var module in Native) module.Dispose();
            Native.Clear();
        }
    }

    internal static void AfterApplicationMain()
    {
        if (_output is null) return;
        Sample("application-main-returned-before-finalizers");
        CollectAndAwaitFinalizers();
        Sample("application-main-returned-after-finalizers");
    }

    private static void CollectAndAwaitFinalizers()
    {
        CollectDiagnostic();
        long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        while (Native.Any(value => value.Read().Any(count => count != 0)) && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(10);
    }

    private static void CollectDiagnostic()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }

    private sealed class NativeCounts : IDisposable
    {
        private readonly nint _library;
        private readonly nint _count;
        internal string Provider { get; }
        internal NativeCounts(string provider, string path)
        {
            Provider = provider; _library = NativeLibrary.Load(path);
            try { _count = NativeLibrary.GetExport(_library, "nw_db_diagnostic_count_v1"); }
            catch { NativeLibrary.Free(_library); throw; }
        }
        internal unsafe ulong[] Read()
        {
            var result = new ulong[6];
            for (uint kind = 0; kind < result.Length; kind++) result[kind] = ((delegate* unmanaged[Cdecl]<uint, ulong>)_count)(kind);
            return result;
        }
        public void Dispose() => NativeLibrary.Free(_library);
    }

    [EventSource(Name = "NativeWeave.B22")]
    private sealed class ControlEvents : EventSource
    {
        internal static readonly ControlEvents Log = new();
        [Event(1, Level = EventLevel.Informational)]
        public void Phase(string phase) => WriteEvent(1, phase);
        [Event(2, Level = EventLevel.Informational)]
        public void Observation(long sequence, string phase) => WriteEvent(2, sequence, phase);
    }
}
