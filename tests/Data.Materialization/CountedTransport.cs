using Korda.Data;
using Korda.Database;
using Korda.Database.Internal;

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
        private ScopedStreamBatch? _last;
        private long _generation;
        private int _size;
        private void ObserveRelease()
        {
            if (_last is null) return;
            Suite.Check(!_last.IsActive && _last.Generation == _generation,
                "Actual scoped batch generation released before next fetch or join");
            _last = null;
            Interlocked.Decrement(ref owner.Batches);
            Interlocked.Add(ref owner.Bytes, -_size);
            Interlocked.Increment(ref owner.ReleasedBatches);
        }
        public async ValueTask<ScopedStreamBatch> FetchAsync()
        {
            ObserveRelease();
            var batch = await inner.FetchAsync();
            Interlocked.Increment(ref owner.Batches);
            int size;
            using (var read = batch.AcquireRead()) size = read.Bytes.Length;
            _last = batch; _generation = batch.Generation; _size = size;
            var current = Interlocked.Add(ref owner.Bytes, size);
            owner.PeakBytes = Math.Max(owner.PeakBytes, current);
            return batch;
        }
        public void CheckReadAdmission() => inner.CheckReadAdmission();
        public void RequestStop() => inner.RequestStop();
        public async Task<StreamRetirement> JoinAsync()
        {
            var result = await inner.JoinAsync();
            Suite.Check(result.NativeRetired, "Actual native retirement receipt");
            if (Interlocked.Exchange(ref _joined, 1) == 0)
            {
                ObserveRelease();
                Interlocked.Decrement(ref owner.Transports); Interlocked.Increment(ref owner.Joins);
            }
            return result;
        }
    }
}
