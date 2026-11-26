using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korda.Data;

internal static class WireValidationTests
{
    private static DbWire _sink;

    internal static void Run(string output)
    {
        var assertions = 0;
        void Check(bool condition, string message)
        { assertions++; if (!condition) throw new InvalidOperationException(message); }
        void Reject(byte[] input, WireExpectation expected)
        {
            foreach (var storage in new[] { 0, 1, 2 })
            {
                var rejected = false;
                try
                {
                    if (storage == 2) _ = DbWire.Validate(input, expected);
                    else _ = storage == 0 ? DbWire.Parse(input, expected) : DbWire.ParseOwned(input, expected);
                }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "Overlapping or out-of-bounds cell ranges were accepted.");
            }
        }
        var random = new Random(0xDB09);
        foreach (var ids in new uint[][] { [0, 1, 2, 3], [1, 4, 19, 100], [19, 3, 7, 0] })
        {
            var expected = new WireExpectation(PayloadKind.Rows, 2, new byte[32], 0, 71,
                ids.Select(value => new WireDescriptor(value, Codecs.Bytes.Descriptor, 6)).ToArray());
            const int cellCount = 16;
            var cells = new ReadOnlyMemory<byte>?[cellCount];
            for (var i = 0; i < cellCount; i++) cells[i] = Codecs.Int64.Encode(i);
            var original = DbWire.Encode(expected, 4, cells);
            var cellsOffset = WireSchema.HeaderBytes + ids.Length * WireSchema.DescriptorBytes;
            var payloadOffset = cellsOffset + cellCount * WireSchema.CellBytes;
            for (var repeat = 0; repeat < 64; repeat++)
            {
                var order = Enumerable.Range(0, cellCount).ToArray();
                if (repeat == 1) Array.Reverse(order);
                if (repeat > 1) random.Shuffle(order.AsSpan());
                var input = (byte[])original.Clone();
                for (var i = 0; i < cellCount; i++)
                    BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(cellsOffset + i * WireSchema.CellBytes + 8), (ulong)(payloadOffset + order[i] * 8));
                foreach (var storage in new[] { 0, 1 })
                {
                    var parsed = storage == 0 ? DbWire.Parse(input, expected) : DbWire.ParseOwned(input, expected);
                    Check(parsed.Rows == 4, "Unordered valid payload changed its row count.");
                    for (var i = 0; i < cellCount; i++)
                        Check(BinaryPrimitives.ReadInt64LittleEndian(parsed.Cell((uint)(i / ids.Length), i % ids.Length)!.Value.Span) == order[i],
                            "Unordered non-overlapping payload changed a value.");
                }
                var spanLayout = DbWire.Validate(input, expected);
                Check(spanLayout.Rows == 4, "Span layout changed its row count.");
                for (var i = 0; i < cellCount; i++)
                    Check(BinaryPrimitives.ReadInt64LittleEndian(spanLayout.Cell(input, (uint)(i / ids.Length), i % ids.Length, out var isNull)) == order[i] && !isNull,
                        "Span layout changed a non-overlapping value.");
                foreach (var overlap in new[] { 0, 4 })
                {
                    var invalid = (byte[])input.Clone();
                    BinaryPrimitives.WriteUInt64LittleEndian(invalid.AsSpan(cellsOffset + 8), (ulong)(payloadOffset + 8));
                    BinaryPrimitives.WriteUInt64LittleEndian(invalid.AsSpan(cellsOffset + WireSchema.CellBytes + 8), (ulong)(payloadOffset + 8 + overlap));
                    Reject(invalid, expected);
                }
            }
            var beyond = (byte[])original.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(beyond.AsSpan(cellsOffset + 8), (ulong)original.Length - 4);
            Reject(beyond, expected);
        }
        var nullable = Codecs.NullableReference(Codecs.Bytes).Descriptor;
        var mixed = new WireExpectation(PayloadKind.Rows, 2, new byte[32], 0, 71,
            [new(0, nullable, 6), new(1, nullable, 6), new(2, nullable, 6), new(3, nullable, 6)]);
        var bytes = DbWire.Encode(mixed, 1, [new byte[] { 1, 2 }, null, ReadOnlyMemory<byte>.Empty, new byte[] { 3 }]);
        var wire = DbWire.ParseOwned(bytes, mixed);
        Check(wire.Cell(0, 1) is null && wire.Cell(0, 2) is { Length: 0 } && wire.Cell(0, 3)!.Value.Span[0] == 3,
            "NULL, empty and adjacent nonempty ranges changed.");
        foreach (var ids in new uint[][] { [0, 1, 1], [1, 2, 2], [3, 1, 3], [1, 2, 0] })
        {
            var expected = new WireExpectation(PayloadKind.Parameters, 2, new byte[32], 0, 0,
                ids.Select(value => new WireDescriptor(value, Codecs.Int64.Descriptor, 3)).ToArray());
            var rejected = false;
            try { _ = DbWire.Encode(expected, 1, [Codecs.Int64.Encode(1), Codecs.Int64.Encode(2), Codecs.Int64.Encode(3)]); }
            catch (ArgumentException error) { rejected = error.Message == "Duplicate/reserved descriptor ID."; }
            Check(rejected, "Duplicate or reserved descriptor validation changed.");
        }
        File.WriteAllText(Path.Combine(output, "wire-validation-assertions.txt"), assertions.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static void RunAllocation(string output)
    {
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Allocation evidence requires Native AOT.");
        const int operations = 256;
        var expected = new WireExpectation(PayloadKind.Rows, 2, new byte[32], 0, 71,
            [new(0, Codecs.Int64.Descriptor, 3), new(1, Codecs.Int64.Descriptor, 3)]);
        var inputs = new List<(int Rows, byte[] Bytes, string Hash)>();
        foreach (var rows in new[] { 0, 1, 128, 4096 })
        {
            var cells = new ReadOnlyMemory<byte>?[rows * 2];
            for (var i = 0; i < cells.Length; i++) cells[i] = Codecs.Int64.Encode(i);
            var bytes = DbWire.Encode(expected, (uint)rows, cells);
            inputs.Add((rows, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }
        var results = new WireValidationAllocation[12];
        foreach (var input in inputs)
            for (var i = 0; i < operations * 4; i++) _sink = DbWire.ParseOwned(input.Bytes, expected);
        _ = Stopwatch.GetTimestamp(); _ = GC.GetTotalAllocatedBytes(true);
        _ = GC.CollectionCount(0); _ = GC.CollectionCount(1); _ = GC.CollectionCount(2);
        var index = 0;
        foreach (var input in inputs)
        for (var repeat = 0; repeat < 3; repeat++)
        {
            var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
            var start = Stopwatch.GetTimestamp(); var before = GC.GetTotalAllocatedBytes(true);
            for (var i = 0; i < operations; i++) _sink = DbWire.ParseOwned(input.Bytes, expected);
            var after = GC.GetTotalAllocatedBytes(true); var end = Stopwatch.GetTimestamp();
            results[index++] = new(input.Rows, input.Bytes.Length, input.Hash, repeat, operations, (long)input.Rows * operations,
                before, after, after - before, GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2,
                end - start, Stopwatch.Frequency);
        }
        var receipt = new WireValidationReceipt("DB-Perf-01", true,
            "Complete validation of already-owned canonical row-major numeric batches. Input arrays, immutable expectations and result slots exist before the interval. No provider fetch, native copy, lease creation, mapping, query or fallback path is included.", results);
        File.WriteAllText(Path.Combine(output, "owned-wire-allocation.json"), JsonSerializer.Serialize(receipt, WireValidationJson.Default.WireValidationReceipt));
        foreach (var result in results)
            if (result.AllocatedBytes != 0) throw new InvalidOperationException($"Owned wire rows={result.RowsPerBatch}: {result.AllocatedBytes} raw bytes / {result.Operations} operations; exact zero required.");
        GC.KeepAlive(_sink.Bytes);
        Console.WriteLine("PASS exact-zero owned row-major batch validation; complete raw counters retained.");
    }
}

internal sealed record WireValidationAllocation(int RowsPerBatch, int EncodedBytes, string InputSha256, int Repeat,
    int Operations, long Rows, long AllocatedBefore, long AllocatedAfter, long AllocatedBytes,
    int Gen0, int Gen1, int Gen2, long ElapsedTicks, long TimerFrequency);
internal sealed record WireValidationReceipt(string Task, bool NativeAot, string Scope, WireValidationAllocation[] Results);
[JsonSerializable(typeof(WireValidationReceipt))]
internal partial class WireValidationJson : JsonSerializerContext { }
