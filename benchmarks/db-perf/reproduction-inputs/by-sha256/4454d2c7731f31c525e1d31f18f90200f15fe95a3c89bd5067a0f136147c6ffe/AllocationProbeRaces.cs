using System.Buffers.Binary;
using Korda.Data;
using Korda.Database.Internal;

// Deterministic lifecycle tests for the lazy active-move join. Every transport
// completion is explicitly released, so a passing test cannot depend on a
// conveniently synchronous provider or a timing delay.
internal static class AllocationProbeRaces
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static int s_checks;

    internal static async Task RunAsync()
    {
        s_checks = 0;
        await DisposeDuringStart();
        await DisposeDuringFetch();
        await DisposeDuringMapping(failMapping: false, failCleanup: false);
        await DisposeDuringMapping(failMapping: true, failCleanup: false);
        await DisposeDuringMapping(failMapping: true, failCleanup: true);
        await CancellationDuringFetch();
        Console.WriteLine($"PASS lazy stream join races: {s_checks} assertions; pending start/fetch, shared disposers, active mapper, primary/cleanup failures and cancellation");
    }

    private static async Task DisposeDuringStart()
    {
        var projection = Projection<long>.Create([ResultField.Create("id", Codecs.Int64)], static row => row.Read(0, Codecs.Int64));
        var transport = new ControlledTransport(projection, pendingFetch: false);
        var started = new TaskCompletionSource<IStreamTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = Stream(projection, (_, _) => started.Task);
        var reader = sequence.GetAsyncEnumerator();
        var move = reader.MoveNextAsync().AsTask();
        Task first = reader.DisposeAsync().AsTask(), second = reader.DisposeAsync().AsTask();
        Check(!move.IsCompleted && !first.IsCompleted && !second.IsCompleted, "start admission and both disposers remain pending");
        try
        {
            started.SetResult(transport);
            await transport.JoinEntered.Task.WaitAsync(Bound);
            Check(!first.IsCompleted && !second.IsCompleted, "disposers wait for actual transport retirement after delayed start");
            Check(transport.Fetches == 0 && transport.Batch.Disposals == 0, "disposal before start completion cannot fetch or dispose an untransferred batch");
            transport.ReleaseJoin();
            await Failure<ObjectDisposedException>(move);
            await Task.WhenAll(first, second).WaitAsync(Bound);
            Check(transport.Joins == 1 && transport.Stops > 0, "delayed start cleanup joins once");
            await reader.DisposeAsync();
            Check(transport.Joins == 1, "repeated disposal observes the same retirement");
        }
        finally { started.TrySetResult(transport); transport.ReleaseJoin(); }
    }

    private static async Task DisposeDuringFetch()
    {
        int mapped = 0;
        var projection = Projection<long>.Create([ResultField.Create("id", Codecs.Int64)], row => { mapped++; return row.Read(0, Codecs.Int64); });
        var transport = new ControlledTransport(projection, pendingFetch: true);
        var reader = Stream(projection, transport.Start).GetAsyncEnumerator();
        var move = reader.MoveNextAsync().AsTask();
        await transport.FetchEntered.Task.WaitAsync(Bound);
        try
        {
            try { _ = reader.MoveNextAsync(); throw new InvalidOperationException("Expected concurrent-use rejection."); }
            catch (DatabaseException error) when (error.Kind == DatabaseErrorKind.ConcurrentUse) { s_checks++; }
            Task first = reader.DisposeAsync().AsTask(), second = reader.DisposeAsync().AsTask();
            Check(!first.IsCompleted && !second.IsCompleted && transport.Batch.Disposals == 0, "both disposers wait without releasing an outstanding fetch batch");
            transport.CompleteFetch();
            await transport.JoinEntered.Task.WaitAsync(Bound);
            Check(!first.IsCompleted && !second.IsCompleted, "fetch completion does not substitute for the retirement join");
            Check(mapped == 0 && transport.Batch.Disposals == 1, "batch is compensated once and never mapped after closure");
            transport.ReleaseJoin();
            await Failure<ObjectDisposedException>(move);
            await Task.WhenAll(first, second).WaitAsync(Bound);
            Check(transport.Joins == 1 && transport.Batch.Disposals == 1, "concurrent cleanup has one batch release and one join");
            try { _ = reader.MoveNextAsync(); throw new InvalidOperationException("Expected disposed rejection."); }
            catch (ObjectDisposedException) { s_checks++; }
        }
        finally { transport.CompleteFetch(); transport.ReleaseJoin(); }
    }

    private static async Task DisposeDuringMapping(bool failMapping, bool failCleanup)
    {
        using var releaseMapper = new ManualResetEventSlim();
        var enteredMapper = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mappingFailure = new InvalidDataException("Controlled business mapper failure.");
        var cleanupFailure = new InvalidOperationException("Controlled cleanup failure after batch release.");
        var projection = Projection<long>.Create([ResultField.Create("id", Codecs.Int64)], row =>
        {
            enteredMapper.TrySetResult();
            if (!releaseMapper.Wait(Bound)) throw new TimeoutException("Mapper release was not delivered.");
            if (failMapping) throw mappingFailure;
            return row.Read(0, Codecs.Int64);
        });
        var transport = new ControlledTransport(projection, pendingFetch: false);
        var reader = Stream(projection, transport.Start).GetAsyncEnumerator();
        Task<bool> move = Task.Run(() => reader.MoveNextAsync().AsTask());
        try
        {
            await enteredMapper.Task.WaitAsync(Bound);
            Task first = reader.DisposeAsync().AsTask(), second = reader.DisposeAsync().AsTask();
            Check(!first.IsCompleted && !second.IsCompleted, "disposal joins a synchronously executing mapper");
            Check(transport.Batch.Disposals == 0 && transport.Joins == 0, "the mapper retains its batch until it exits");
            releaseMapper.Set();
            await transport.JoinEntered.Task.WaitAsync(Bound);
            Check(transport.Batch.Disposals == 1 && !first.IsCompleted && !second.IsCompleted, "mapper exit releases the batch and still awaits retirement");
            transport.ReleaseJoin(failCleanup ? cleanupFailure : null);
            if (failMapping)
            {
                var failure = await Failure<InvalidDataException>(move);
                Check(ReferenceEquals(failure, mappingFailure), "cleanup preserves the original mapper exception instance");
                if (failCleanup)
                {
                    Check(ReferenceEquals(failure.Data["Korda.Database.StreamCleanup.v1"], cleanupFailure), "cleanup failure remains attached to the primary mapper failure");
                    Check(ReferenceEquals(await Failure<InvalidOperationException>(first), cleanupFailure), "first disposer observes the cleanup failure");
                    Check(ReferenceEquals(await Failure<InvalidOperationException>(second), cleanupFailure), "second disposer observes the same cleanup failure");
                }
                else await Task.WhenAll(first, second).WaitAsync(Bound);
            }
            else
            {
                Check(await move.WaitAsync(Bound), "an already admitted synchronous mapper retains its successful row");
                await Task.WhenAll(first, second).WaitAsync(Bound);
            }
            Check(transport.Joins == 1 && transport.Batch.Disposals == 1, "mapper race retirement is once-only");
        }
        finally { releaseMapper.Set(); transport.ReleaseJoin(); }
    }

    private static async Task CancellationDuringFetch()
    {
        using var cancellation = new CancellationTokenSource();
        var projection = Projection<long>.Create([ResultField.Create("id", Codecs.Int64)], static row => row.Read(0, Codecs.Int64));
        var transport = new ControlledTransport(projection, pendingFetch: true);
        var reader = Stream(projection, transport.Start, cancellation.Token).GetAsyncEnumerator();
        var move = reader.MoveNextAsync().AsTask();
        try
        {
            await transport.FetchEntered.Task.WaitAsync(Bound);
            cancellation.Cancel();
            Check(transport.Stops > 0 && !move.IsCompleted, "cancellation requests stop without inventing a terminal fetch result");
            var disposing = reader.DisposeAsync().AsTask();
            transport.CancelFetch(cancellation.Token);
            await transport.JoinEntered.Task.WaitAsync(Bound);
            Check(!disposing.IsCompleted && !move.IsCompleted, "confirmed fetch cancellation still joins native-equivalent retirement");
            transport.ReleaseJoin();
            var failure = await Failure<OperationCanceledException>(move);
            Check(failure.CancellationToken == cancellation.Token, "cancellation retains its admitted token");
            await disposing.WaitAsync(Bound);
            Check(transport.Joins == 1 && transport.Batch.Disposals == 1, "canceled untransferred batch is compensated once");
        }
        finally { transport.CancelFetch(cancellation.Token); transport.ReleaseJoin(); }
    }

    private static TypedStream<long> Stream(Projection<long> projection, Func<int, CancellationToken, Task<IStreamTransport>> start,
        CancellationToken token = default) => new(start, projection, 2, 71, [3], new(2, 4096, 2, 4096), token);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Interlocked.Increment(ref s_checks);
    }
    private static async Task<TException> Failure<TException>(Task task) where TException : Exception
    {
        try { await task.WaitAsync(Bound); }
        catch (TException error) { s_checks++; return error; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name);
    }

    private sealed class ControlledTransport : IStreamTransport
    {
        private readonly Task<IStreamTransport> _start;
        private readonly Task<IStreamBatch> _ready;
        private readonly TaskCompletionSource<IStreamBatch>? _fetch;
        private readonly TaskCompletionSource<StreamRetirement> _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FetchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource JoinEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly OwnedBatch Batch;
        internal int Fetches, Stops, Joins;
        internal ControlledTransport(Projection<long> projection, bool pendingFetch)
        {
            byte[] scalar = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(scalar, 42);
            var expectation = new WireExpectation(PayloadKind.Rows, 2, Convert.FromHexString(projection.ShapeHash), 0, 71,
                [new(0, Codecs.Int64.Descriptor, 3)]);
            Batch = new(DbWire.Encode(expectation, 1, [new ReadOnlyMemory<byte>(scalar)]));
            _start = Task.FromResult<IStreamTransport>(this); _ready = Task.FromResult<IStreamBatch>(Batch);
            if (pendingFetch) _fetch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal Task<IStreamTransport> Start(int rows, CancellationToken token) => _start;
        public Task<IStreamBatch> FetchAsync()
        {
            if (Interlocked.Increment(ref Fetches) != 1) throw new InvalidOperationException("Race fixture fetched twice.");
            Batch.Acquire(); FetchEntered.TrySetResult(); return _fetch?.Task ?? _ready;
        }
        internal void CompleteFetch() => _fetch?.TrySetResult(Batch);
        internal void CancelFetch(CancellationToken token)
        {
            if (_fetch is { Task.IsCompleted: false }) { Batch.Dispose(); _fetch.TrySetCanceled(token); }
        }
        public void RequestStop() => Interlocked.Increment(ref Stops);
        public Task<StreamRetirement> JoinAsync()
        {
            Check(!Batch.Active, "transport retirement begins after batch release");
            Interlocked.Increment(ref Joins); JoinEntered.TrySetResult(); return _retirement.Task;
        }
        internal void ReleaseJoin(Exception? failure = null)
        {
            if (failure is null) _retirement.TrySetResult(new(true, true, 0));
            else _retirement.TrySetException(failure);
        }
    }
    private sealed class OwnedBatch(byte[] bytes) : IStreamBatch
    {
        private int _active;
        internal int Disposals;
        internal bool Active => Volatile.Read(ref _active) != 0;
        internal void Acquire() => Volatile.Write(ref _active, 1);
        public ReadOnlyMemory<byte> Bytes => Active ? bytes : throw new ObjectDisposedException(nameof(OwnedBatch));
        public bool End => false;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) throw new InvalidOperationException("Race fixture batch was released twice.");
            Interlocked.Increment(ref Disposals);
        }
    }
}
