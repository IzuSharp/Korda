using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.Internal;

// Test-only accounting wraps the actual B04 transport; it never manufactures a row,
// terminal callback, native retirement receipt or connection-reuse decision.
internal sealed class CountedResources
{
    internal int Transports, Batches, ReleasedBatches, Joins;
    internal long Bytes, PeakBytes;
    internal TypedStream<T> Stream<T>(DataSession session, SqlCommand command, Projection<T> projection, CancellationToken token = default)
    {
        var source = session.Source;
        var projectionId = source.RegisterProjection(projection);
        var codecs = projection.Fields.Select(field => source.BindCodec(field.Descriptor)).ToArray();
        return new(async (rows, cancellation) =>
        {
            using var plan = SqlSubmission.Create(session, command, 3, projectionId);
            var transport = await session.OpenPlanStreamAsync(plan, ReadOnlyMemory<byte>.Empty, rows, cancellation);
            Interlocked.Increment(ref Transports);
            return new CountedTransport(transport, this);
        }, projection, source.Binding.SemanticProfile, projectionId, codecs, session.StreamingLimits, token);
    }
    internal void AssertRetired()
    {
        Suite.Check(Transports == 0 && Batches == 0 && Bytes == 0, "Managed transport/batch/charged bytes retired: " + string.Join(',', Transports, Batches, Bytes));
    }
    private sealed class CountedTransport(IStreamTransport inner, CountedResources owner) : IStreamTransport
    {
        private int _joined;
        public async Task<IStreamBatch> FetchAsync()
        {
            var batch = await inner.FetchAsync();
            Interlocked.Increment(ref owner.Batches);
            var size = batch.Bytes.Length; var current = Interlocked.Add(ref owner.Bytes, size);
            owner.PeakBytes = Math.Max(owner.PeakBytes, current);
            return new CountedBatch(batch, owner, size);
        }
        public void RequestStop() => inner.RequestStop();
        public async Task<StreamRetirement> JoinAsync()
        {
            var result = await inner.JoinAsync();
            Suite.Check(result.NativeRetired, "Actual native retirement receipt");
            if (Interlocked.Exchange(ref _joined, 1) == 0) { Interlocked.Decrement(ref owner.Transports); Interlocked.Increment(ref owner.Joins); }
            return result;
        }
    }
    private sealed class CountedBatch(IStreamBatch inner, CountedResources owner, int size) : IStreamBatch
    {
        private int _disposed;
        public ReadOnlyMemory<byte> Bytes => inner.Bytes;
        public bool End => inner.End;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            inner.Dispose(); Interlocked.Decrement(ref owner.Batches); Interlocked.Add(ref owner.Bytes, -size); Interlocked.Increment(ref owner.ReleasedBatches);
        }
    }
}
