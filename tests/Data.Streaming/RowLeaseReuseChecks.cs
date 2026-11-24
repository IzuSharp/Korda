using System.Buffers.Binary;
using Korda.Data;
using Korda.Database.Internal;

// Test-only access to internal leases does not grant a public reader any
// lifetime beyond its synchronous materializer. No fixture owner is reused.
internal static class RowLeaseReuseChecks
{
    private static int s_checks;
    private static readonly ValueCodec<long?> NullableLong = Codecs.Nullable(Codecs.Int64);
    private static readonly Projection<long?> Shape = Projection<long?>.Create(
        [ResultField.Create("id", NullableLong)], static row => row.Read(0, NullableLong));
    private static readonly WireExpectation Expected = new(PayloadKind.Rows, 2,
        Convert.FromHexString(Shape.ShapeHash), 0, 71, [new(0, NullableLong.Descriptor, 3)]);
    private static readonly StreamLimits Limits = new(8, 4096, 32, 16 * 1024);

    internal static async Task RunAsync()
    {
        s_checks = 0;
        foreach (var storage in new[] { "owned", "unmarked", "native" })
        {
            ReattachAndCompensate(storage);
            await IndependentEnumerators(storage);
            await MalformedLaterBatch(storage);
            await ReentrantMapping(storage);
        }
        Console.WriteLine($"PASS row lease reuse: {s_checks} assertions; inactive checks, ownership compensation, later malformed batches, independent enumerators and reentrant mapper rejection across owned/unmarked/native storage");
    }

    private static byte[] Encode(params long?[] values)
    {
        var cells = new ReadOnlyMemory<byte>?[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var bytes = NullableLong.Encode(values[i]);
            if (bytes is null) cells[i] = null;
            else cells[i] = new ReadOnlyMemory<byte>(bytes);
            if (values[i].HasValue != cells[i].HasValue || cells[i] is { } cell && cell.Length != 8)
                throw new InvalidOperationException("Fixture lost SQL NULL or Int64 width before wire encoding.");
        }
        return DbWire.Encode(Expected, (uint)values.Length, cells);
    }

    private static ITrackedBatch Owner(string storage, byte[] bytes, bool end = false)
    {
        ITrackedBatch result = storage switch
        {
            "native" => new BorrowedBatchFixture(bytes, end),
            "owned" => new OwnedBatch(bytes, end),
            _ => new ManagedBatch(bytes, end),
        };
        result.Acquire();
        return result;
    }

    private static void ReattachAndCompensate(string storage)
    {
        var first = Owner(storage, Encode(1, 2));
        var lease = new RowBatchLease(first, Expected, Shape.Fields, Limits);
        Check(lease.IsActive && lease.Rows == 2 && lease.Materialize(1, Shape) == 2, "initial row lease failed");
        var unaccepted = Owner(storage, Encode(99));
        Throws<InvalidOperationException>(() => lease.Attach(unaccepted, Expected, Limits));
        Check(first.Disposals == 0 && unaccepted.Disposals == 0 && first.Active && unaccepted.Active,
            "active-lease rejection transferred or released an owner");
        if (unaccepted is OwnedBatch untouched) Check(untouched.Takes == 0, "precondition rejection took incoming bytes");
        Check(lease.Materialize(0, Shape) == 1, "active-lease rejection changed the previous row");
        unaccepted.Dispose(); Released(unaccepted);
        lease.Dispose(); lease.Dispose(); Released(first); Inactive(lease);

        var second = Owner(storage, Encode(null, 301, 302));
        lease.Attach(second, Expected, Limits);
        Check(lease.IsActive && lease.Rows == 3 && lease.Value(0) is null && !lease.End,
            "attachment did not reset row zero or nullable state");
        Check(lease.Materialize(0, Shape) is null && lease.Materialize(2, Shape) == 302,
            "different-sized attachment materialized stale cells");
        lease.Dispose(); Released(second); Inactive(lease);
        var empty = Owner(storage, Encode(), end: true);
        lease.Attach(empty, Expected, Limits);
        Check(lease.Rows == 0 && lease.End, "terminal empty attachment changed");
        lease.Dispose(); Released(empty); Inactive(lease);

        foreach (var failure in new[] { "header", "range", "shape", "byte-budget", "row-budget", "empty-not-end" })
        {
            var bytes = Encode(41, 42); var limits = Limits;
            if (failure == "header") bytes = bytes[..95];
            if (failure == "range") BinaryPrimitives.WriteUInt64LittleEndian(
                bytes.AsSpan(WireSchema.HeaderBytes + WireSchema.DescriptorBytes + 8), ulong.MaxValue);
            if (failure == "shape") BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), 72);
            if (failure == "byte-budget") limits = limits with { MaxBatchBytes = bytes.Length - 1 };
            if (failure == "row-budget") limits = limits with { MaxBatchRows = 1 };
            if (failure == "empty-not-end") bytes = Encode();
            var incoming = Owner(storage, bytes);
            var error = Throws<DatabaseException>(() => lease.Attach(incoming, Expected, limits));
            Check(error.Kind == (failure == "byte-budget" ? DatabaseErrorKind.ResourceLimit : DatabaseErrorKind.ShapeMismatch),
                "attachment changed failure classification: " + failure);
            if (failure is "header" or "range" or "shape")
                Check(error.InnerException is ArgumentException, "wire parser failure lost its original wrapper");
            if (incoming is OwnedBatch transferred) Check(transferred.Takes == 1, "failed attachment transferred bytes more than once");
            Released(incoming); Inactive(lease);
            lease.Dispose(); Released(incoming);
        }

        var bytesAfterFailure = Encode(611);
        var afterFailure = Owner(storage, bytesAfterFailure);
        lease.Attach(afterFailure, Expected, Limits);
        if (storage == "unmarked") Array.Clear(bytesAfterFailure);
        Check(!lease.End && lease.Rows == 1 && lease.Materialize(0, Shape) == 611,
            "failed attachments left stale state or unmarked input lost defensive isolation");
        lease.Dispose(); Released(afterFailure); Inactive(lease);
    }

    private static TypedStream<long?> Stream(Projection<long?> shape, BatchSequence transport) =>
        new(transport.Start, shape, 2, 71, [3], Limits);

    private static async Task IndependentEnumerators(string storage)
    {
        var leftOwners = new[] { Owner(storage, Encode(11)), Owner(storage, Encode(12), end: true) };
        var rightOwners = new[] { Owner(storage, Encode(101, 102)), Owner(storage, Encode(103), end: true) };
        var leftTransport = new BatchSequence(leftOwners); var rightTransport = new BatchSequence(rightOwners);
        await using var left = Stream(Shape, leftTransport).GetAsyncEnumerator();
        await using var right = Stream(Shape, rightTransport).GetAsyncEnumerator();
        Check(await left.MoveNextAsync() && left.Current == 11, "left first batch failed");
        Check(await right.MoveNextAsync() && right.Current == 101, "right first batch failed");
        Check(await left.MoveNextAsync() && left.Current == 12, "left reattachment failed");
        Released(leftOwners[0]);
        Check(rightOwners[0].Disposals == 0 && rightOwners[0].Active,
            "reattaching one enumerator retired another enumerator's batch");
        await left.DisposeAsync();
        Check(await right.MoveNextAsync() && right.Current == 102, "disposal of the left enumerator invalidated the right lease");
        Check(await right.MoveNextAsync() && right.Current == 103 && !await right.MoveNextAsync(),
            "independent right reattachment or terminal row changed");
        await right.DisposeAsync();
        foreach (var owner in leftOwners.Concat(rightOwners)) Released(owner);
        Check(leftTransport.Joins == 1 && rightTransport.Joins == 1,
            "independent enumerators did not join exactly once");
    }

    private static async Task MalformedLaterBatch(string storage)
    {
        var malformed = Encode(2);
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(72), 72);
        var owners = new[] { Owner(storage, Encode(1)), Owner(storage, malformed) };
        var transport = new BatchSequence(owners);
        await using var reader = Stream(Shape, transport).GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current == 1, "valid first batch failed before later corruption");
        DatabaseException? failure = null;
        try { _ = await reader.MoveNextAsync(); }
        catch (DatabaseException error) { failure = error; }
        Check(failure is { Kind: DatabaseErrorKind.ShapeMismatch, InnerException: ArgumentException },
            "malformed later attachment did not preserve shape failure");
        Check(!await reader.MoveNextAsync() && transport.Fetches == 2 && transport.Joins == 1,
            "failed reattachment continued fetching or skipped cleanup");
        await reader.DisposeAsync();
        foreach (var owner in owners) Released(owner);
    }

    private static async Task ReentrantMapping(string storage)
    {
        IAsyncEnumerator<long?>? reader = null;
        var rejected = 0;
        var shape = Projection<long?>.Create(Shape.Fields, row =>
        {
            try { _ = reader!.MoveNextAsync(); }
            catch (DatabaseException error) when (error.Kind == DatabaseErrorKind.ConcurrentUse) { rejected++; }
            return row.Read(0, NullableLong);
        });
        var owners = new[] { Owner(storage, Encode(71)), Owner(storage, Encode(72), end: true) };
        var transport = new BatchSequence(owners);
        reader = Stream(shape, transport).GetAsyncEnumerator();
        try
        {
            Check(await reader.MoveNextAsync() && reader.Current == 71, "reentrant first mapper result changed");
            Check(await reader.MoveNextAsync() && reader.Current == 72, "reentrant reused mapper result changed");
            Check(!await reader.MoveNextAsync() && rejected == 2, "a mapper reentered an active attach/move");
        }
        finally { await reader.DisposeAsync(); }
        foreach (var owner in owners) Released(owner);
    }

    private static void Inactive(RowBatchLease lease)
    {
        Check(!lease.IsActive, "retired or failed lease still reports active");
        Throws<ObjectDisposedException>(lease.AssertAlive);
        Throws<ObjectDisposedException>(() => { _ = lease.Rows; });
        Throws<ObjectDisposedException>(() => { _ = lease.Value(0); });
        Throws<ObjectDisposedException>(() => { _ = lease.RowBytes(0); });
        Throws<ObjectDisposedException>(() => { _ = lease.Materialize(0, Shape); });
    }
    private static void Released(ITrackedBatch owner) => Check(owner.Disposals == 1 && !owner.Active
        && (owner is not BorrowedBatchFixture || owner.NativeReleases == 1), "batch owner was not released exactly once");
    private static void Check(bool condition, string message)
    { s_checks++; if (!condition) throw new InvalidOperationException(message); }
    private static TException Throws<TException>(Action action) where TException : Exception
    {
        try { action(); } catch (TException error) { s_checks++; return error; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name);
    }

    private sealed class BatchSequence(ITrackedBatch[] owners) : IStreamTransport
    {
        internal int Fetches, Joins;
        public Task<IStreamTransport> Start(int rows, CancellationToken token) => Task.FromResult<IStreamTransport>(this);
        public ValueTask<IStreamBatch> FetchAsync() => Fetches < owners.Length
            ? new(owners[Fetches++]) : throw new InvalidOperationException("No fixture batch remains.");
        public void RequestStop() { }
        public Task<StreamRetirement> JoinAsync() { Joins++; return Task.FromResult(new StreamRetirement(true, true, 0)); }
    }
    private class ManagedBatch(byte[] bytes, bool end) : ITrackedBatch
    {
        protected byte[]? Payload = bytes;
        private bool _active;
        public bool Active => _active;
        public int Disposals { get; private set; }
        public int NativeReleases => 0;
        public void Acquire()
        { if (_active || Disposals != 0) throw new InvalidOperationException("Fixture owner acquired twice."); _active = true; }
        public ReadOnlyMemory<byte> Bytes => Active && Payload is { } current ? current : throw new ObjectDisposedException(nameof(ManagedBatch));
        public bool End => end;
        public void Dispose()
        { if (!_active) throw new InvalidOperationException("Fixture owner released twice."); Disposals++; _active = false; Payload = null; }
    }
    private sealed class OwnedBatch(byte[] bytes, bool end) : ManagedBatch(bytes, end), IOwnedStreamBatch
    {
        internal int Takes;
        public byte[] TakeBytes()
        {
            var result = Interlocked.Exchange(ref Payload, null) ?? throw new InvalidOperationException("Fixture bytes transferred twice.");
            Takes++; return result;
        }
    }
}
