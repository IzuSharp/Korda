using System.Runtime.InteropServices;
using Korda.Data;
using Korda.Database.Internal;

internal static class StableBatchTests
{
    internal static void Run()
    {
        var shape = Projection<long>.Create([ResultField.Create("id", Codecs.Int64)], row => row.Read(0, Codecs.Int64));
        var expected = new WireExpectation(PayloadKind.Rows, 2, Convert.FromHexString(shape.ShapeHash), 0, 71,
            [new(0, Codecs.Int64.Descriptor, 3)]);
        var limits = new StreamLimits(8, 4096, 8, 4096);
        byte[] Encoded() => DbWire.Encode(expected, 1, [Codecs.Int64.Encode(42)]);
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void RejectDisposed(Action action)
        {
            try { action(); } catch (ObjectDisposedException) { return; }
            throw new InvalidOperationException("Disposed row lease remained usable.");
        }

        var bytes = Encoded();
        var owner = new StableBatch(bytes);
        var lease = new RowBatchLease(owner, expected, shape.Fields, limits);
        Check(lease.Materialize(0, shape) == 42, "Stable batch mapping changed.");
        var encodedValue = lease.Value(0)!.Value;
        Check(MemoryMarshal.TryGetArray(encodedValue, out var storage) && ReferenceEquals(storage.Array, bytes),
            "Stable host storage was copied again.");
        lease.Dispose(); lease.Dispose();
        Check(owner.Disposals == 1 && Codecs.Int64.Decode(encodedValue) == 42, "Disposal changed stable managed bytes or released twice.");
        RejectDisposed(() => lease.Materialize(0, shape));
        RejectDisposed(() => lease.Value(0));

        var mutable = Encoded();
        var ordinary = new OrdinaryBatch(mutable);
        using (var snapshot = new RowBatchLease(ordinary, expected, shape.Fields, limits))
        {
            Check(snapshot.Materialize(0, shape) == 42, "Snapshot batch mapping changed.");
            var cell = snapshot.Value(0)!.Value;
            Check(MemoryMarshal.TryGetArray(cell, out storage) && !ReferenceEquals(storage.Array, mutable),
                "Unmarked transport bypassed the defensive copy.");
            Array.Clear(mutable);
            Check(snapshot.Materialize(0, shape) == 42, "Unmarked caller mutation reached a validated snapshot.");
        }
        Check(ordinary.Disposals == 1, "Fallback owner was not released once.");

        foreach (var failure in new[] { "header", "row-limit", "byte-limit", "empty-not-terminal" })
        {
            var invalid = Encoded(); var selected = limits;
            if (failure == "header") invalid[4] = 2;
            if (failure == "row-limit") selected = limits with { MaxBatchRows = 0 };
            if (failure == "byte-limit") selected = limits with { MaxBatchBytes = invalid.Length - 1 };
            if (failure == "empty-not-terminal") invalid = DbWire.Encode(expected, 0, []);
            var invalidOwner = new StableBatch(invalid);
            var rejected = false;
            try { using var unused = new RowBatchLease(invalidOwner, expected, shape.Fields, selected); }
            catch (DatabaseException) { rejected = true; }
            Check(rejected && invalidOwner.Disposals == 1, "Invalid stable batch lost validation or compensation: " + failure);
        }
        Console.WriteLine("PASS stable managed batch storage, defensive fallback, disposal and malformed-batch compensation");
    }

    private sealed class StableBatch(byte[] bytes) : IStableManagedBatch
    {
        internal int Disposals;
        public byte[] OwnedBytes => Disposals == 0 ? bytes : throw new ObjectDisposedException(nameof(StableBatch));
        public ReadOnlyMemory<byte> Bytes => OwnedBytes;
        public bool End => false;
        public void Dispose() => Disposals++;
    }
    private sealed class OrdinaryBatch(byte[] bytes) : IStreamBatch
    {
        internal int Disposals;
        public ReadOnlyMemory<byte> Bytes => Disposals == 0 ? bytes : throw new ObjectDisposedException(nameof(OrdinaryBatch));
        public bool End => false;
        public void Dispose() => Disposals++;
    }
}
