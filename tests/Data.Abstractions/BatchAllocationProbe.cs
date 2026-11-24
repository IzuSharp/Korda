using System.Text.Json;
using System.Text.Json.Serialization;
using Korda.Data;

// Both paths perform the same production validation. Only the explicit immutable
// ownership contract changes. This isolates the second managed array copy; it
// does not claim a public query or a whole batch is allocation-free.
internal static class BatchAllocationProbe
{
    private static DbWire? _sink;
    private static byte[]? _arraySink;
    internal static void Run(string output)
    {
        const int iterations = 256;
        var expected = new WireExpectation(PayloadKind.Rows, 2, new byte[32], 0, 71,
            [new(0, Codecs.Int64.Descriptor, 3), new(1, Codecs.Int64.Descriptor, 3)]);
        var receipts = new List<BatchCopyMeasurement>(12);
        var inputs = new List<(int Rows, byte[] Bytes)>();
        foreach (var rows in new[] { 0, 1, 128, 4096 })
        {
            var cells = new ReadOnlyMemory<byte>?[rows * 2];
            for (var i = 0; i < cells.Length; i++) cells[i] = Codecs.Int64.Encode(i);
            inputs.Add((rows, DbWire.Encode(expected, (uint)rows, cells)));
        }
        // Exercise large-array and GC/runtime initialization before all intervals.
        foreach (var (_, bytes) in inputs)
            for (var i = 0; i < iterations * 4; i++) { _sink = DbWire.Parse(bytes, expected); _sink = DbWire.ParseOwned(bytes, expected); _arraySink = bytes.AsSpan().ToArray(); }
        _ = GC.GetTotalAllocatedBytes(true);
        foreach (var (rows, bytes) in inputs)
        {
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var before0 = GC.CollectionCount(0); var before1 = GC.CollectionCount(1); var before2 = GC.CollectionCount(2);
                var startCopy = GC.GetTotalAllocatedBytes(true);
                for (var i = 0; i < iterations; i++) _sink = DbWire.Parse(bytes, expected);
                var endCopy = GC.GetTotalAllocatedBytes(true);
                var copy0 = GC.CollectionCount(0); var copy1 = GC.CollectionCount(1); var copy2 = GC.CollectionCount(2);
                var startOwned = GC.GetTotalAllocatedBytes(true);
                for (var i = 0; i < iterations; i++) _sink = DbWire.ParseOwned(bytes, expected);
                var endOwned = GC.GetTotalAllocatedBytes(true);
                var owned0 = GC.CollectionCount(0); var owned1 = GC.CollectionCount(1); var owned2 = GC.CollectionCount(2);
                var startArray = GC.GetTotalAllocatedBytes(true);
                for (var i = 0; i < iterations; i++) _arraySink = bytes.AsSpan().ToArray();
                var endArray = GC.GetTotalAllocatedBytes(true);
                var removed = (endCopy - startCopy) - (endOwned - startOwned);
                receipts.Add(new(rows, bytes.Length, repeat, iterations, startCopy, endCopy, startOwned, endOwned,
                    startArray, endArray, removed, endArray - startArray,
                    [copy0 - before0, copy1 - before1, copy2 - before2],
                    [owned0 - copy0, owned1 - copy1, owned2 - copy2],
                    [GC.CollectionCount(0) - owned0, GC.CollectionCount(1) - owned1, GC.CollectionCount(2) - owned2]));
            }
        }
        File.WriteAllText(Path.Combine(output, "batch-copy-allocation.json"), JsonSerializer.Serialize(receipts.ToArray(), BatchCopyJson.Default.BatchCopyMeasurementArray));
        foreach (var receipt in receipts)
            if (receipt.RemovedBytes <= 0)
                throw new InvalidOperationException($"Rows={receipt.Rows}, repeat={receipt.Repeat}: no observed allocation reduction. Raw counts were preserved.");
        GC.KeepAlive(_sink);
        GC.KeepAlive(_arraySink);
        // Large allocation loops can trigger different GC/runtime work. The raw
        // whole-process difference need not equal the separate array-only control;
        // no control is subtracted to claim exact framework attribution or zero.
        Console.WriteLine("PASS stable managed batch: defensive copy allocation is explicit; raw parser controls and GC counts retained.");
    }
}
internal sealed record BatchCopyMeasurement(int Rows, int EncodedBytes, int Repeat, int Operations,
    long CopyBefore, long CopyAfter, long OwnedBefore, long OwnedAfter, long ArrayOnlyBefore, long ArrayOnlyAfter,
    long RemovedBytes, long SeparateArrayCopyControlBytes, int[] CopyCollections, int[] OwnedCollections, int[] ArrayOnlyCollections);
[JsonSerializable(typeof(BatchCopyMeasurement[]))]
internal partial class BatchCopyJson : JsonSerializerContext { }
