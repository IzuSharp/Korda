using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korda.Data;
using Korda.Database.Internal;

internal interface ITrackedBatch : IStreamBatch
{
    int Disposals { get; }
    int NativeReleases { get; }
    bool Active { get; }
    void Acquire();
}

// This fixture uses actual unmanaged storage and SafeHandle finalization. It
// exercises the same borrowed-memory seam without making a provider race depend
// on the database worker's scheduling.
internal sealed class BorrowedBatchFixture(byte[] input, bool end = false, BorrowedReleaseCounter? counter = null)
    : MemoryManager<byte>, IBorrowedStreamBatch, ITrackedBatch
{
    private NativeOwner? _owner;
    private int _active, _disposals;
    private readonly BorrowedReleaseCounter _counter = counter ?? new();
    public int Disposals => _disposals;
    public int NativeReleases => Volatile.Read(ref _counter.Releases);
    public bool Active => Volatile.Read(ref _active) != 0;
    public void Acquire()
    {
        if (Interlocked.Exchange(ref _active, 1) != 0 || _owner is not null) throw new InvalidOperationException("Fixture may be acquired only once.");
        _owner = new(input, _counter);
    }
    public SafeHandle ReadOwner => Active && _owner is { } owner ? owner : throw new ObjectDisposedException(nameof(BorrowedBatchFixture));
    public ReadOnlyMemory<byte> Bytes { get { _ = ReadOwner; return CreateMemory(input.Length); } }
    public bool End => end;
    public override unsafe Span<byte> GetSpan()
    {
        var owner = ReadOwner;
        ObjectDisposedException.ThrowIf(owner.IsClosed, this);
        return new Span<byte>((void*)owner.DangerousGetHandle(), input.Length);
    }
    public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
    public override void Unpin() { }
    public void Dispose() => Dispose(true);
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _active, 0) == 0) throw new InvalidOperationException("Fixture batch was released twice.");
        _owner!.Dispose();
        Interlocked.Increment(ref _disposals);
    }
    private sealed class NativeOwner : SafeHandle
    {
        private readonly BorrowedReleaseCounter _counter;
        internal unsafe NativeOwner(byte[] input, BorrowedReleaseCounter counter) : base(0, true)
        {
            _counter = counter;
            SetHandle((nint)NativeMemory.Alloc((nuint)Math.Max(1, input.Length)));
            if (handle == 0) throw new OutOfMemoryException();
            input.CopyTo(new Span<byte>((void*)handle, input.Length));
        }
        public override bool IsInvalid => handle == 0;
        protected override unsafe bool ReleaseHandle()
        {
            NativeMemory.Free((void*)handle);
            Interlocked.Increment(ref _counter.Releases);
            return true;
        }
    }
}

internal sealed class BorrowedReleaseCounter
{
    internal int Releases;
}

internal static class BorrowedBatchChecks
{
    private static int s_checks;
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); s_checks++; }
    internal static void Run()
    {
        s_checks = 0;
        ScopeAndAbandonment();
        ValidationAndOwnedResults();
        TypedJsonOwnership();
        Console.WriteLine($"PASS borrowed native batch lifetime and validation: {s_checks} assertions; bounded native ownership, complete synchronous reads, finalizer fallback and independent decoded values");
    }
    private static void ScopeAndAbandonment()
    {
        var batch = new BorrowedBatchFixture([42]); batch.Acquire();
        var memory = batch.Bytes;
        using (var read = new BatchReadScope(batch.ReadOwner))
        {
            var bytes = memory.Span;
            Check(!MemoryMarshal.TryGetArray(memory, out _), "Borrowed fixture unexpectedly owns a managed copy.");
            batch.Dispose();
            GC.Collect(); GC.WaitForPendingFinalizers();
            Check(batch.NativeReleases == 0 && bytes[0] == 42, "The complete synchronous span read lost its SafeHandle reference.");
        }
        Check(batch.NativeReleases == 1, "The last synchronous read did not release native memory exactly once.");
        var rejected = false;
        try { _ = memory.Span.Length; } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "A stale borrowed memory value remained readable after disposal.");
        var counter = new BorrowedReleaseCounter();
        var orphan = Abandon(counter);
        for (var i = 0; i < 10 && counter.Releases == 0; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!orphan.IsAlive && counter.Releases == 1, "An abandoned native batch leaked a persistent SafeHandle reference.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(BorrowedReleaseCounter counter)
    {
        var batch = new BorrowedBatchFixture([1, 2, 3], counter: counter); batch.Acquire();
        using (var read = new BatchReadScope(batch.ReadOwner)) Check(batch.Bytes.Span[2] == 3, "Abandonment fixture read failed.");
        return new WeakReference(batch);
    }
    private static void ValidationAndOwnedResults()
    {
        var projection = Projection<OwnedResult>.Create(
            [ResultField.Create("id", Codecs.Int64), ResultField.Create("name", Codecs.String), ResultField.Create("bytes", Codecs.Bytes)],
            static row => new(row.Read(0, Codecs.Int64), row.Read(1, Codecs.String), row.Read(2, Codecs.Bytes)));
        var fields = projection.Fields.ToArray();
        var expected = new WireExpectation(PayloadKind.Rows, 2, Convert.FromHexString(projection.ShapeHash), 0, 71,
            [new(0, Codecs.Int64.Descriptor, 3), new(1, Codecs.String.Descriptor, 5), new(2, Codecs.Bytes.Descriptor, 6)]);
        var valid = DbWire.Encode(expected, 1, [Codecs.Int64.Encode(42), Codecs.String.Encode("retained"), new byte[] { 1, 2, 255 }]);
        var batch = new BorrowedBatchFixture(valid); batch.Acquire();
        OwnedResult result;
        using (var lease = new RowBatchLease(batch, expected, fields, new(2, 4096, 2, 4096)))
        {
            Check(lease.RowBytes(0) == 3 * WireSchema.CellBytes + 8 + 8 + 3, "Borrowed row byte accounting changed.");
            result = lease.Materialize(0, projection);
            Check(batch.NativeReleases == 0 && result.Id == 42, "Materializer did not retain the native batch.");
        }
        Check(batch.Disposals == 1 && batch.NativeReleases == 1, "A successful lease did not release its owner exactly once.");
        GC.Collect(); GC.WaitForPendingFinalizers();
        Check(result.Name == "retained" && result.Bytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 255 }), "Decoded application values retained borrowed storage.");
        Reject(valid[..95], expected, fields, new(2, 4096, 2, 4096), false, DatabaseErrorKind.ShapeMismatch);
        var malformed = (byte[])valid.Clone(); malformed[0] = 0;
        Reject(malformed, expected, fields, new(2, 4096, 2, 4096), false, DatabaseErrorKind.ShapeMismatch);
        Reject(valid, expected, fields, new(2, valid.Length - 1, 2, 4096), false, DatabaseErrorKind.ResourceLimit);
        var empty = DbWire.Encode(expected, 0, []);
        Reject(empty, expected, fields, new(2, 4096, 2, 4096), false, DatabaseErrorKind.ShapeMismatch);
        var tooMany = DbWire.Encode(expected, 2, [Codecs.Int64.Encode(1), Codecs.String.Encode("a"), new byte[] { 1 }, Codecs.Int64.Encode(2), Codecs.String.Encode("b"), new byte[] { 2 }]);
        Reject(tooMany, expected, fields, new(1, 4096, 2, 4096), false, DatabaseErrorKind.ShapeMismatch);
        var eos = new BorrowedBatchFixture(empty, end: true); eos.Acquire();
        using (var lease = new RowBatchLease(eos, expected, fields, new(2, 4096, 2, 4096)))
            Check(lease.End && lease.Rows == 0, "Borrowed empty EOS was rejected.");
        Check(eos.NativeReleases == 1, "EOS native owner leaked.");
    }
    private static void Reject(byte[] input, WireExpectation expected, ResultField[] fields, StreamLimits limits, bool end, DatabaseErrorKind kind)
    {
        var batch = new BorrowedBatchFixture(input, end); batch.Acquire();
        var rejected = false;
        try { using var lease = new RowBatchLease(batch, expected, fields, limits); }
        catch (DatabaseException error) when (error.Kind == kind) { rejected = true; }
        Check(rejected && batch.Disposals == 1 && batch.NativeReleases == 1, "Borrowed constructor failure did not compensate its owner exactly once.");
    }
    private static void TypedJsonOwnership()
    {
        var codec = Codecs.Json(BorrowedBatchJson.Default.BorrowedJsonValue);
        var projection = Projection<BorrowedJsonValue>.Create([ResultField.Create("json", codec)], row => row.Read(0, codec));
        var expected = new WireExpectation(PayloadKind.Rows, 2, Convert.FromHexString(projection.ShapeHash), 0, 72, [new(0, codec.Descriptor, 16)]);
        var input = DbWire.Encode(expected, 1, [codec.Encode(new(42, ["owned", "values"]))]);
        var batch = new BorrowedBatchFixture(input); batch.Acquire();
        BorrowedJsonValue value;
        using (var lease = new RowBatchLease(batch, expected, projection.Fields, new(1, 4096, 1, 4096)))
            value = lease.Materialize(0, projection);
        GC.Collect(); GC.WaitForPendingFinalizers();
        Check(batch.NativeReleases == 1 && value.Id == 42 && value.Labels.SequenceEqual(new[] { "owned", "values" }), "Typed JSON retained borrowed native storage after deserialization.");
        var wrongShape = new BorrowedBatchFixture(DbWire.Encode(expected, 1, ["[]"u8.ToArray()])); wrongShape.Acquire();
        var failed = false;
        try
        {
            using var lease = new RowBatchLease(wrongShape, expected, projection.Fields, new(1, 4096, 1, 4096));
            _ = lease.Materialize(0, projection);
        }
        catch (Exception error) when (error is JsonException or DatabaseException) { failed = true; }
        Check(failed && wrongShape.NativeReleases == 1 && wrongShape.Disposals == 1, "A typed JSON mapping failure leaked the borrowed owner.");
    }
    private sealed record OwnedResult(long Id, string Name, byte[] Bytes);
}

internal sealed record BorrowedJsonValue(long Id, string[] Labels);
[JsonSerializable(typeof(BorrowedJsonValue))]
internal partial class BorrowedBatchJson : JsonSerializerContext { }
