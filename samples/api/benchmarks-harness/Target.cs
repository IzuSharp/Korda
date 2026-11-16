using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NativeWeave.Benchmarks.Fixtures;

/// <summary>Explicit test target for scalar adapter overhead, outside every application package.</summary>
internal static partial class Target
{
#if NATIVE_ROUTE
    private const string Route = "nativeweave-aot-rust";

    [LibraryImport("nativeweave_benchmark_fixture", EntryPoint = "nw_benchmark_mix_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint NativeMix(ulong value, out ulong result);
#else
    private const string Route = "managed-aot";
#endif

    private static ulong Mix(ulong value)
    {
#if NATIVE_ROUTE
        uint status = NativeMix(value, out ulong result);
        if (status != 0)
        {
            throw new InvalidOperationException($"Fixture returned status {status}.");
        }
        return result;
#else
        for (int i = 0; i < 8; i++)
        {
            value = unchecked((value ^ (value >> 27)) * 0x3c79ac492ba7b653UL);
        }
        return value;
#endif
    }

    private static int Main()
    {
        Write(new TargetEvent { Kind = "hello", Route = Route, DynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported });
        while (Console.ReadLine() is { } line)
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 3 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out int id) ||
                !ulong.TryParse(parts[1], CultureInfo.InvariantCulture, out ulong value))
            {
                Console.Error.WriteLine("Expected id/value/mode fixture input.");
                return 2;
            }
            Write(new TargetEvent { Kind = "admitted", Id = id });
            if (parts[2] == "timeout")
            {
                continue;
            }
            string status = parts[2] is "ok" or "failed" or "rejected" or "cancelled" ? parts[2] : "unsupported";
            long before = GC.GetAllocatedBytesForCurrentThread();
            ulong result = Mix(value);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Dictionary<string, CounterObservation> counters = Counters(allocated);
            Write(new TargetEvent { Kind = "completed", Id = id, Status = status, Value = result, Counters = counters });
        }
        return 0;
    }

    private static void Write(TargetEvent value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, TargetJsonContext.Default.TargetEvent));
        Console.Out.Flush();
    }

    private static Dictionary<string, CounterObservation> Counters(long allocated)
    {
        string[] bytes = ["managed_allocated_bytes", "native_allocated_bytes", "copied_bytes", "pinned_bytes",
            "retained_bytes", "byte_credits", "gc_heap_live_bytes", "gc_heap_committed_bytes",
            "native_allocator_live_bytes", "native_allocator_retained_bytes"];
        string[] counts = ["native_allocations", "ffi_calls", "live_owners", "live_permits", "callback_roots",
            "operations", "queue_slots", "connections", "runtime_instances"];
        Dictionary<string, CounterObservation> result = [];
        foreach (string key in bytes)
        {
            result[key] = new(null, "bytes", "unknown", "Fixture has no domain instrumentation for this counter");
        }
        foreach (string key in counts)
        {
            result[key] = new(null, "count", "unknown", "Fixture has no domain instrumentation for this counter");
        }
        result["managed_allocated_bytes"] = new(allocated, "bytes", "measured",
            "GC.GetAllocatedBytesForCurrentThread delta around scalar operation; excludes protocol and controller");
        result["ffi_calls"] = new(Route == "nativeweave-aot-rust" ? 1 : 0, "count", "derived",
            "One direct fixture P/Invoke for the native route, zero for managed scalar");
        return result;
    }
}

internal sealed class TargetEvent
{
    public required string Kind { get; init; }
    public string? Route { get; init; }
    public bool? DynamicCodeSupported { get; init; }
    public int? Id { get; init; }
    public string? Status { get; init; }
    public ulong? Value { get; init; }
    public Dictionary<string, CounterObservation>? Counters { get; init; }
}

internal sealed record CounterObservation(long? Value, string Unit, string Availability, string Method);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TargetEvent))]
internal partial class TargetJsonContext : JsonSerializerContext;
