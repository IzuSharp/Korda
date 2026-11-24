using System.Runtime.CompilerServices;
using Korda.Data;

namespace Korda.Database.Internal;

// Shape metadata only: no session, factory, cancellation or cursor state can be
// retained here. The prepared owner publishes one immutable layout after its
// first successful stream-construction validation.
internal sealed class StreamProjectionLayout(object projection, WireExpectation expectation, int minimumBatchBytes)
{
    internal object Projection { get; } = projection;
    internal WireExpectation Expectation { get; } = expectation;
    internal int MinimumBatchBytes { get; } = minimumBatchBytes;
}

/// <summary>One-shot typed stream adapter used by the Database host and query terminal owners.</summary>
/// <remarks>Creation validates and freezes the projection identity without dispatch. The factory captures an
/// already-snapshotted command and starts only at first MoveNext. Both cancellation tokens are linked.
/// One batch is retained; mapping is synchronous, produces owned CLR values, and has no borrowed span across await.
/// This internal assembly does not define a second DataSession, QuerySelection, runtime, or NuGet package.</remarks>
internal sealed class TypedStream<T> : IAsyncEnumerable<T>
{
    private readonly Func<int, CancellationToken, Task<IStreamTransport>> _start;
    private readonly Projection<T> _projection;
    private readonly WireExpectation _expected;
    private readonly StreamLimits _limits;
    private readonly CancellationToken _methodToken;
    private int _enumerated;

    internal TypedStream(Func<int, CancellationToken, Task<IStreamTransport>> start, Projection<T> projection,
        uint profile, uint projectionId, ReadOnlySpan<uint> boundCodecs, StreamLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(projection);
        limits.Validate();
        _expected = CreateExpectation(projection, profile, projectionId, boundCodecs, out var minimumBatchBytes);
        ValidateBatchCapacity(minimumBatchBytes, limits);
        _start = start; _projection = projection; _limits = limits; _methodToken = cancellationToken;
    }

    internal TypedStream(Func<int, CancellationToken, Task<IStreamTransport>> start, Projection<T> projection,
        StreamProjectionLayout layout, StreamLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(projection);
        limits.Validate();
        ArgumentNullException.ThrowIfNull(layout);
        if (!ReferenceEquals(layout.Projection, projection)) throw new ArgumentException("The frozen stream layout belongs to another projection.", nameof(layout));
        ValidateBatchCapacity(layout.MinimumBatchBytes, limits);
        _expected = layout.Expectation;
        _start = start; _projection = projection; _limits = limits; _methodToken = cancellationToken;
    }

    internal static StreamProjectionLayout CreateProjectionLayout(Projection<T> projection, uint profile, uint projectionId,
        ReadOnlySpan<uint> boundCodecs, StreamLimits limits)
    {
        ArgumentNullException.ThrowIfNull(projection);
        limits.Validate();
        var expectation = CreateExpectation(projection, profile, projectionId, boundCodecs, out var minimumBatchBytes);
        ValidateBatchCapacity(minimumBatchBytes, limits);
        return new(projection, expectation, minimumBatchBytes);
    }

    private static WireExpectation CreateExpectation(Projection<T> projection, uint profile, uint projectionId,
        ReadOnlySpan<uint> boundCodecs, out int minimumBatchBytes)
    {
        if (boundCodecs.Length != projection.Fields.Length) throw new ArgumentException("Bound codec count differs from the exact projection.", nameof(boundCodecs));
        var descriptors = new WireDescriptor[boundCodecs.Length];
        var configured = new Dictionary<uint, CodecDescriptor>();
        for (var i = 0; i < descriptors.Length; i++)
        {
            var codec = projection.Fields[i].Descriptor;
            if (profile == 1 && codec.Id == 13 || profile == 2 && codec.Id == 14)
                throw new DatabaseCapabilityException("selected-result-codec", profile == 1 ? "sqlite" : "postgresql");
            if (codec.Configuration is not null)
            {
                if (configured.TryGetValue(boundCodecs[i], out var previous) && !previous.SameScalar(codec))
                    throw new ArgumentException("A source-bound codec ID aliases different scalar definitions.", nameof(boundCodecs));
                configured[boundCodecs[i]] = codec;
            }
            descriptors[i] = new((uint)i, codec, boundCodecs[i]);
        }
        var expectation = new WireExpectation(PayloadKind.Rows, profile, Convert.FromHexString(projection.ShapeHash), 0, projectionId, descriptors);
        var empty = DbWire.Encode(expectation, 0, Array.Empty<ReadOnlyMemory<byte>?>());
        minimumBatchBytes = checked(empty.Length + descriptors.Length * WireSchema.CellBytes);
        return expectation;
    }

    private static void ValidateBatchCapacity(int minimumBatchBytes, StreamLimits limits)
    {
        if (minimumBatchBytes > limits.MaxBatchBytes)
            throw new DatabaseException("LargeValueRequiresStreaming: a single row descriptor table exceeds the batch limit.", DatabaseErrorKind.ResourceLimit, "LargeValueRequiresStreaming");
    }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => CreateEnumerator(cancellationToken, _limits.MaxBatchRows, false);
    private Enumerator CreateEnumerator(CancellationToken cancellationToken, int fetchRows, bool buffered)
    {
        if (Interlocked.Exchange(ref _enumerated, 1) != 0) throw new InvalidOperationException("A native result stream can be enumerated only once.");
        return new(this, cancellationToken, fetchRows, buffered);
    }
    /// <summary>Consumes a bounded owned list; no silent fallback to streaming on capacity failure.</summary>
    internal Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default) => BufferAsync(cancellationToken);
    private async Task<IReadOnlyList<T>> BufferAsync(CancellationToken token)
    {
        await using var reader = CreateEnumerator(token, _limits.MaxBatchRows, true);
        var result = new List<T>(Math.Min(_limits.MaxBufferedRows, _limits.MaxBatchRows));
        while (await reader.MoveNextAsync().ConfigureAwait(false)) result.Add(reader.Current);
        return result;
    }
    /// <summary>Creates a bounded owned array using the same row/byte policy as ToList.</summary>
    internal async Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default) => (await BufferAsync(cancellationToken).ConfigureAwait(false)).ToArray();
    /// <summary>Reads at most one business row and joins cursor cleanup; absence throws.</summary>
    internal async Task<T> FirstAsync(CancellationToken cancellationToken = default)
    {
        await using var reader = CreateEnumerator(cancellationToken, 1, false);
        if (!await reader.MoveNextAsync().ConfigureAwait(false)) throw new InvalidOperationException("The result contains no rows.");
        return reader.Current;
    }
    /// <summary>Reads at most one business row; absence is default(T), including value types.</summary>
    internal async Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        await using var reader = CreateEnumerator(cancellationToken, 1, false);
        return await reader.MoveNextAsync().ConfigureAwait(false) ? reader.Current : default;
    }
    /// <summary>Reads at most two business rows; exactly one row is required.</summary>
    internal async Task<T> SingleAsync(CancellationToken cancellationToken = default)
    {
        await using var reader = CreateEnumerator(cancellationToken, Math.Min(2, _limits.MaxBatchRows), false);
        if (!await reader.MoveNextAsync().ConfigureAwait(false)) throw new InvalidOperationException("The result contains no rows.");
        var value = reader.Current;
        if (await reader.MoveNextAsync().ConfigureAwait(false)) throw new InvalidOperationException("The result contains more than one row.");
        return value;
    }
    /// <summary>Requires at most one row and returns default(T) for absence.</summary>
    internal async Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        await using var reader = CreateEnumerator(cancellationToken, Math.Min(2, _limits.MaxBatchRows), false);
        if (!await reader.MoveNextAsync().ConfigureAwait(false)) return default;
        var value = reader.Current;
        if (await reader.MoveNextAsync().ConfigureAwait(false)) throw new InvalidOperationException("The result contains more than one row.");
        return value;
    }

    private sealed class Enumerator : IAsyncEnumerator<T>
    {
        private readonly TypedStream<T> _parent;
        private readonly CancellationTokenSource? _linked;
        private readonly CancellationToken _token;
        private readonly int _fetchRows;
        private readonly bool _buffered;
        private readonly object _gate = new();
        private bool _moving;
        private TaskCompletionSource? _moveJoin;
        private TaskCompletionSource? _cleanup;
        private IStreamTransport? _transport;
        private CancellationTokenRegistration _registration;
        private RowBatchLease? _batch;
        private uint _row;
        private bool _lastBatch, _closed, _finished;
        private long _bufferedBytes;
        private int _bufferedRows;
        private Exception? _controlFailure;
        private T _current = default!;

        internal Enumerator(TypedStream<T> parent, CancellationToken token, int fetchRows, bool buffered)
        {
            _parent = parent; _fetchRows = fetchRows; _buffered = buffered;
            if (parent._methodToken.CanBeCanceled && token.CanBeCanceled && parent._methodToken != token)
            { _linked = CancellationTokenSource.CreateLinkedTokenSource(parent._methodToken, token); _token = _linked.Token; }
            else _token = parent._methodToken.CanBeCanceled ? parent._methodToken : token;
        }
        public T Current => _current;
        public ValueTask<bool> MoveNextAsync()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_moving) throw new DatabaseException("Another MoveNext is active.", DatabaseErrorKind.ConcurrentUse);
                _moving = true;
            }
            return MoveAsync();
        }
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<bool> MoveAsync()
        {
            try
            {
                if (_finished) return false;
                _token.ThrowIfCancellationRequested();
                if (_transport is null)
                {
                    var transport = await _parent._start(_fetchRows, _token).ConfigureAwait(false);
                    lock (_gate) _transport = transport;
                    _registration = _token.UnsafeRegister(static state => ((Enumerator)state!).RequestStop(), this);
                    bool closed; lock (_gate) closed = _closed;
                    if (closed) { RequestStop(); throw new ObjectDisposedException(nameof(Enumerator)); }
                }
                if (_batch is null || _row == _batch.Rows)
                {
                    _batch?.Dispose(); _batch = null;
                    if (_lastBatch) { _finished = true; await CleanupAsync().ConfigureAwait(false); return false; }
                    var owner = await _transport.FetchAsync().ConfigureAwait(false);
                    // Constructor owns compensation if validation fails.
                    _batch = new(owner, _parent._expected, _parent._projection.Fields, _parent._limits with { MaxBatchRows = _fetchRows });
                    _row = 0; _lastBatch = _batch.End;
                    if (_buffered)
                    {
                        var overhead = checked(WireSchema.HeaderBytes + _parent._projection.Fields.Length * (long)WireSchema.DescriptorBytes);
                        if (overhead > _parent._limits.MaxBufferedBytes - _bufferedBytes)
                            throw new DatabaseException("Buffered descriptor bytes exceed the result limit.", DatabaseErrorKind.ResourceLimit);
                        _bufferedBytes += overhead;
                    }
                    if (_batch.Rows == 0) { _finished = true; await CleanupAsync().ConfigureAwait(false); return false; }
                }
                bool closing; lock (_gate) closing = _closed;
                if (closing) throw new ObjectDisposedException(nameof(Enumerator));
                if (_buffered)
                {
                    var bytes = _batch.RowBytes(_row);
                    if (_bufferedRows >= _parent._limits.MaxBufferedRows || bytes > _parent._limits.MaxBufferedBytes - _bufferedBytes)
                        throw new DatabaseException("Buffered result exceeds its row or charged-byte limit.", DatabaseErrorKind.ResourceLimit);
                    _bufferedRows++; _bufferedBytes += bytes;
                }
                _current = _batch.Materialize(_row++, _parent._projection);
                return true;
            }
            catch (Exception original)
            {
                _finished = true;
                try { await CleanupAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { original.Data["Korda.Database.StreamCleanup.v1"] = cleanup; }
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _moving = false;
                    var join = _moveJoin; _moveJoin = null;
                    join?.TrySetResult();
                }
            }
        }
        private Task CleanupAsync()
        {
            TaskCompletionSource completion;
            lock (_gate)
            {
                if (_cleanup is not null) return _cleanup.Task;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _cleanup = completion;
            }
            _ = CompleteCleanupAsync(completion);
            return completion.Task;
        }
        private async Task CompleteCleanupAsync(TaskCompletionSource completion)
        {
            Exception? failure = null;
            try
            {
                _batch?.Dispose(); _batch = null;
                await _registration.DisposeAsync().ConfigureAwait(false);
                if (_transport is { } transport)
                {
                    RequestStop();
                    var retired = await transport.JoinAsync().ConfigureAwait(false);
                    if (!retired.NativeRetired) throw new DatabaseException("Native stream cleanup remains pending.", DatabaseErrorKind.InvalidState, "CleanupPending");
                }
                if (_controlFailure is { } controlFailure) throw new DatabaseException("Native stream stop control failed.", DatabaseErrorKind.InvalidState, innerException: controlFailure);
            }
            catch (Exception exception) { failure = exception; }
            finally { _linked?.Dispose(); }
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
        public ValueTask DisposeAsync()
        {
            Task active;
            lock (_gate)
            {
                _closed = true;
                // Normal row moves need no separate completion owner. Only an
                // overlapping Dispose waits here, sharing one immutable Task
                // until the active move has finished mapping and cleanup.
                active = _moving ? (_moveJoin ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.CompletedTask;
            }
            RequestStop();
            return new(DisposeAfterMoveAsync(active));
        }
        private void RequestStop()
        {
            IStreamTransport? transport; lock (_gate) transport = _transport;
            try { transport?.RequestStop(); }
            catch (Exception failure) { Interlocked.CompareExchange(ref _controlFailure, failure, null); }
        }
        private async Task DisposeAfterMoveAsync(Task active)
        {
            await active.ConfigureAwait(false);
            await CleanupAsync().ConfigureAwait(false);
        }
    }
}
