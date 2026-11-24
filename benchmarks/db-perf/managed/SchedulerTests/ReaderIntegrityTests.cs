using System.Threading.Tasks.Sources;

internal static class ReaderIntegrityTests
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static void Hash(ref Digest digest, long value) => digest.Integer(value);

    internal static void Run()
    {
        Console.WriteLine($"READER_CONTROL_PROCESS pid={Environment.ProcessId} managed_thread_id={Environment.CurrentManagedThreadId}");
        foreach (var moveAsync in new[] { false, true })
        foreach (var closeAsync in new[] { false, true })
        foreach (var site in new[] { "move", "current", "hash" })
        {
            var oracleBefore = CorrectnessOracle.Failures;
            Exception primary = site == "move" ? new FormatException("Injected typed read failure.")
                : site == "current" ? new OverflowException("Injected typed Current failure.")
                : new InvalidDataException("Injected row hash failure.");
            var cleanup = new IOException("Injected reader retirement failure.");
            var stream = new ControlledStream(moveAsync, closeAsync)
            {
                MoveFailure = site == "move" ? primary : null,
                CurrentFailure = site == "current" ? primary : null,
                CloseFailure = cleanup,
            };
            RowDigest<long> hash = site == "hash" ? (ref Digest digest, long value) => throw primary : Hash;
            var pending = ReadStream.Consume(stream, hash);
            stream.Complete(pending);
            Exception? observed = null;
            try { _ = pending.GetAwaiter().GetResult(); }
            catch (Exception error) { observed = error; }
            Require(observed is AggregateException combined && combined.InnerExceptions.Count == 2
                && ReferenceEquals(combined.InnerExceptions[0], primary)
                && ReferenceEquals(combined.InnerExceptions[1], cleanup)
                && CorrectnessOracle.Failures > oracleBefore,
                "A reader retirement failure masked the original hard read/current/hash failure.");
            Require(stream.Disposals == 1 && !stream.Active, "Reader failure did not retire exactly once.");
        }
        var singleFailure = new FormatException("Primary failure with successful cleanup.");
        var single = new ControlledStream(true, true) { MoveFailure = singleFailure };
        var singlePending = ReadStream.Consume(single, Hash);
        single.Complete(singlePending);
        try { _ = singlePending.GetAwaiter().GetResult(); throw new InvalidOperationException("Primary failure disappeared."); }
        catch (FormatException error) { Require(ReferenceEquals(error, singleFailure), "Primary exception identity changed."); }
        Require(single.Disposals == 1 && !single.Active, "Single failure leaked or double-retired the reader.");
        foreach (var closeAsync in new[] { false, true })
        {
            var cleanup = new IOException("Cleanup-only failure.");
            var stream = new ControlledStream(true, closeAsync) { CloseFailure = cleanup };
            var pending = ReadStream.Consume(stream, Hash);
            stream.Complete(pending);
            try { _ = pending.GetAwaiter().GetResult(); throw new InvalidOperationException("Cleanup-only failure disappeared."); }
            catch (IOException error) { Require(ReferenceEquals(error, cleanup), "Cleanup-only exception identity changed."); }
            Require(stream.Disposals == 1 && !stream.Active, "Cleanup-only failure double-retired the reader.");
        }
        var creationFailure = new InvalidOperationException("Enumerator creation failure.");
        var notCreated = new ControlledStream(false, false) { CreationFailure = creationFailure };
        var absent = ReadStream.Consume(notCreated, Hash);
        try { _ = absent.GetAwaiter().GetResult(); throw new InvalidOperationException("Creation failure disappeared."); }
        catch (InvalidOperationException error) { Require(ReferenceEquals(error, creationFailure), "Creation failure identity changed."); }
        Require(notCreated.Disposals == 0 && !notCreated.Active, "A failed construction pretended to own a reader.");

        // Retain a negative control for the old await-finally shape.
        var old = new ControlledStream(true, true)
        { MoveFailure = new FormatException("Old primary"), CloseFailure = new IOException("Old cleanup") };
        var oldPending = LegacyRead(old, Hash);
        old.Complete(oldPending);
        try { _ = oldPending.GetAwaiter().GetResult(); throw new InvalidOperationException("Old negative control unexpectedly succeeded."); }
        catch (IOException error) { Require(ReferenceEquals(error, old.CloseFailure), "Old masking control changed."); }
        Console.WriteLine("PASS reader read/current/hash plus cleanup failures preserve primary identity, sticky oracle count and single retirement");

        // First-GC/finalizer initialization belongs to the recorded warmup, not
        // preferentially to whichever A/B window first crosses a GC threshold.
        // No bytes are subtracted from any measured window.
        var initializationBefore = GC.GetTotalAllocatedBytes(true);
        var initializationGen0 = GC.CollectionCount(0); var initializationGen1 = GC.CollectionCount(1); var initializationGen2 = GC.CollectionCount(2);
        var initializing = new ControlledStream(true, true);
        for (var i = 0; i < 20000; i++) { Execute(initializing, false, int.MaxValue); Execute(initializing, true, int.MaxValue); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var initializationAfter = GC.GetTotalAllocatedBytes(true);
        Console.WriteLine($"READER_WARMUP operations_per_side=20000 process_start={initializationBefore} process_end={initializationAfter} " +
            $"gc0={GC.CollectionCount(0)-initializationGen0} gc1={GC.CollectionCount(1)-initializationGen1} gc2={GC.CollectionCount(2)-initializationGen2}");

        const int operations = 5000;
        var windows = new Window[24];
        var window = 0;
        var normalCostNotIncreased = true;
        foreach (var moveAsync in new[] { false, true })
        foreach (var closeAsync in new[] { false, true })
        foreach (var early in new[] { false, true })
        {
            var stream = new ControlledStream(moveAsync, closeAsync);
            var stopAfter = early ? 1 : int.MaxValue;
            for (var i = 0; i < 512; i++)
            { Execute(stream, false, stopAfter); Execute(stream, true, stopAfter); }
            for (var repetition = 0; repetition < 3; repetition++)
            {
                var before = Measure(stream, false, stopAfter, operations);
                var after = Measure(stream, true, stopAfter, operations);
                normalCostNotIncreased &= after.ThreadAllocated <= before.ThreadAllocated
                    && after.ProcessEnd - after.ProcessStart <= before.ProcessEnd - before.ProcessStart;
                windows[window++] = new(moveAsync, closeAsync, early, repetition, before, after);
            }
        }
        foreach (var item in windows)
            Console.WriteLine($"READER_ALLOCATION move_async={item.MoveAsync} close_async={item.CloseAsync} early={item.Early} repetition={item.Repetition} " +
                $"operations={operations} rows={operations} before_process_start={item.Before.ProcessStart} before_process_end={item.Before.ProcessEnd} " +
                $"before_thread_bytes={item.Before.ThreadAllocated} after_process_start={item.After.ProcessStart} after_process_end={item.After.ProcessEnd} " +
                $"after_thread_bytes={item.After.ThreadAllocated} before_gc0={item.Before.Gen0} before_gc1={item.Before.Gen1} before_gc2={item.Before.Gen2} " +
                $"after_gc0={item.After.Gen0} after_gc1={item.After.Gen1} after_gc2={item.After.Gen2}");
        Require(normalCostNotIncreased,
            "Preserving reader failures increased normal-path current-thread or whole-process managed allocation.");
        Console.WriteLine("PASS reader normal EOS and early-stop allocation controls; real pending move/dispose continuations completed inline by the caller, no worker-thread allocation attribution claim");
    }

    private static void Execute(ControlledStream stream, bool candidate, int stopAfter)
    {
        var pending = candidate ? ReadStream.Consume(stream, Hash, stopAfter) : LegacyRead(stream, Hash, stopAfter);
        stream.Complete(pending);
        var result = pending.GetAwaiter().GetResult();
        var expected = Digest.Create(); expected.Integer(42);
        Require(result.Rows == 1 && result.Digest == expected.Value
            && result.Suspensions == (stream.MoveAsync ? 1 : 0) + (stream.CloseAsync ? 1 : 0)
            && !stream.Active, "Successful reader control changed rows, suspension count or ownership.");
    }

    private static Reading Measure(ControlledStream stream, bool candidate, int stopAfter, int operations)
    {
        var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
        var processStart = GC.GetTotalAllocatedBytes(true);
        var threadStart = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < operations; i++) Execute(stream, candidate, stopAfter);
        var threadEnd = GC.GetAllocatedBytesForCurrentThread();
        var processEnd = GC.GetTotalAllocatedBytes(true);
        return new(processStart, processEnd, threadEnd - threadStart,
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
    }

    // Exact pre-fix control flow from the active a245/e8e7a4e Korda Read<T>.
    // It is intentionally retained only as a failure/allocation control.
    private static async ValueTask<OperationResult> LegacyRead<T>(IAsyncEnumerable<T> stream, RowDigest<T> hash, int stopAfter = int.MaxValue)
    {
        var digest = Digest.Create(); long rows = 0, suspensions = 0;
        var reader = stream.GetAsyncEnumerator();
        try
        {
            while (rows < stopAfter)
            {
                var move = reader.MoveNextAsync();
                if (!move.IsCompleted) suspensions++;
                if (!await move.ConfigureAwait(false)) break;
                hash(ref digest, reader.Current); rows++;
            }
        }
        finally
        {
            var close = reader.DisposeAsync();
            if (!close.IsCompleted) suspensions++;
            await close.ConfigureAwait(false);
        }
        return new(rows, digest.Value, suspensions);
    }

    private readonly record struct Reading(long ProcessStart, long ProcessEnd, long ThreadAllocated, int Gen0, int Gen1, int Gen2);
    private readonly record struct Window(bool MoveAsync, bool CloseAsync, bool Early, int Repetition, Reading Before, Reading After);

    private sealed class ControlledStream(bool moveAsync, bool closeAsync) : IAsyncEnumerable<long>, IAsyncEnumerator<long>, IValueTaskSource<bool>, IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _move;
        private ManualResetValueTaskSourceCore<bool> _close;
        private bool _movePending, _closePending;
        private int _moves;
        internal readonly bool MoveAsync = moveAsync, CloseAsync = closeAsync;
        internal Exception? CreationFailure, MoveFailure, CurrentFailure, CloseFailure;
        internal bool Active;
        internal int Disposals;
        public long Current => CurrentFailure is { } error ? throw error : 42;
        public IAsyncEnumerator<long> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (CreationFailure is { } creation) throw creation;
            Require(!Active && !_movePending && !_closePending, "The synthetic reader was reused before retirement.");
            Active = true; _moves = 0;
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            if (_moves++ != 0) return ValueTask.FromResult(false);
            if (!MoveAsync) return MoveFailure is { } error ? throw error : ValueTask.FromResult(true);
            _move.Reset(); _movePending = true;
            return new(this, _move.Version);
        }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (!CloseAsync)
            {
                Active = false;
                return CloseFailure is { } error ? throw error : ValueTask.CompletedTask;
            }
            _close.Reset(); _closePending = true;
            return new(this, _close.Version);
        }
        internal void Complete(ValueTask<OperationResult> pending)
        {
            for (var steps = 0; !pending.IsCompleted; steps++)
            {
                Require(steps < 2, "Synthetic pending reader did not have a bounded completion path.");
                if (_movePending)
                {
                    _movePending = false;
                    if (MoveFailure is { } error) _move.SetException(error); else _move.SetResult(true);
                }
                else if (_closePending)
                {
                    _closePending = false;
                    if (CloseFailure is { } error) _close.SetException(error); else _close.SetResult(true);
                }
                else throw new InvalidOperationException("A supposedly suspended read has no pending source.");
            }
        }
        bool IValueTaskSource<bool>.GetResult(short token) => _move.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _move.GetStatus(token);
        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _move.OnCompleted(continuation, state, token, flags);
        void IValueTaskSource.GetResult(short token)
        { try { _ = _close.GetResult(token); } finally { Active = false; } }
        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _close.GetStatus(token);
        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _close.OnCompleted(continuation, state, token, flags);
    }
}
