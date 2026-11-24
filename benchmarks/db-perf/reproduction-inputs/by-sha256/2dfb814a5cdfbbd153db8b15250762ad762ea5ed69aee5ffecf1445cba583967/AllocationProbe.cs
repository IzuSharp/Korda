using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korda.Data;

// Isolates production scalar validation. This is not a provider or whole-ORM zero-GC claim.
internal static class AllocationProbe
{
    private const int Iterations = 100_000;
    private static decimal _sink;

    internal static void Run(string output, bool regression)
    {
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Allocation evidence requires Native AOT.");
        var text = Validation.Utf8.GetBytes("order-12345: café / 订单 / 😀");
        var number = DecimalBytes.Encode(792281625142643375935439503.35m);
        var zero = DecimalBytes.Encode(0m);
        var json = Validation.Utf8.GetBytes("{\"channel\":\"web\",\"gift\":false,\"tags\":[1,null,\"订单\"]}");
        var descriptor = new CodecDescriptor(16, "json", null, false);
        var decimal18 = Codecs.Decimal(18, 2);
        var decimal29 = Codecs.Decimal(29, 2);
        var decimal18Bytes = decimal18.Encode(12345678901234.56m)!;
        var money = Codecs.FixedScaleMoney(2);
        var moneyBytes = money.Encode(12345678901234.56m)!;
        var probes = new (string Name, Action Run, bool ExactZero)[]
        {
            ("empty-driver", static () => { }, true),
            ("strict-utf8-validation", () => DbWire.ValidateScalar(Codecs.String.Descriptor, text), true),
            ("decimal96-canonical-decode", () => _sink = DecimalBytes.Decode(number), true),
            ("decimal-zero-canonical-decode", () => _sink = DecimalBytes.Decode(zero), true),
            ("typed-json-validation", () => DbWire.ValidateScalar(descriptor, json), true),
            ("configured-decimal18-validation", () => DbWire.ValidateScalar(decimal18.Descriptor, decimal18Bytes), false),
            ("configured-decimal29-validation", () => DbWire.ValidateScalar(decimal29.Descriptor, number), false),
            ("configured-money-validation", () => DbWire.ValidateScalar(money.Descriptor, moneyBytes), false),
            ("configured-decimal29-decode", () => _sink = decimal29.Decode(number), false)
        };
        var results = new ScalarAllocation[probes.Length * 3];
        // Exercise runtime and codec initialization outside each measured interval.
        foreach (var probe in probes)
            for (var i = 0; i < Iterations; i++) probe.Run();
        _ = GC.GetTotalAllocatedBytes(true);
        _ = Stopwatch.GetTimestamp();
        // A forced collection here resumes finalizer/runtime work inside the
        // first interval. Leave steady-state GC behavior unchanged instead.
        _ = GC.CollectionCount(0); _ = GC.CollectionCount(1); _ = GC.CollectionCount(2);
        var index = 0;
        foreach (var probe in probes)
        {
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
                var start = Stopwatch.GetTimestamp();
                var before = GC.GetTotalAllocatedBytes(true);
                for (var i = 0; i < Iterations; i++) probe.Run();
                var after = GC.GetTotalAllocatedBytes(true);
                var end = Stopwatch.GetTimestamp();
                results[index++] = new(probe.Name, repeat, Iterations, before, after, after - before,
                    GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2,
                    end - start, Stopwatch.Frequency, probe.ExactZero);
            }
        }
        var receipt = new ScalarReceipt("DB-Perf-01", "internal-scalar-validation-only", true,
            "whole-process cumulative allocation counters; scalar input/output bytes prepared before interval; no DTO materialization", results);
        File.WriteAllText(Path.Combine(output, "scalar-allocation.json"), JsonSerializer.Serialize(receipt, ScalarJson.Default.ScalarReceipt));
        if (regression)
            foreach (var result in results)
                if (result.ZeroRegression && result.AllocatedBytes != 0)
                    throw new InvalidOperationException($"{result.Name}: {result.AllocatedBytes} cumulative bytes / {result.Operations} operations; exact zero required.");
        Console.WriteLine("SCALAR_ALLOCATION_RESULT " + Path.Combine(output, "scalar-allocation.json"));
        GC.KeepAlive(_sink);
    }
}

internal sealed record ScalarAllocation(string Name, int Repeat, int Operations, long AllocatedBefore,
    long AllocatedAfter, long AllocatedBytes, int Gen0, int Gen1, int Gen2, long ElapsedTicks, long TimerFrequency, bool ZeroRegression);
internal sealed record ScalarReceipt(string Task, string Scope, bool NativeAot, string Boundary, ScalarAllocation[] Results);
[JsonSerializable(typeof(ScalarReceipt))]
internal partial class ScalarJson : JsonSerializerContext { }
