using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Data;
using Korda.Database;
using Korda.Database.Internal;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Callbacks;
using Korda.Query.Internal;

// Real provider/callback integration. The proxy forwards the actual request and
// ready notification, changing only when a test barrier lets that callback run
// or return. It neither supplies result bytes nor selects a different drain path.
internal static class ReadySchedulingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static int _checks;
    private const string Sql = "WITH RECURSIVE items(id) AS (SELECT CAST(1 AS BIGINT) UNION ALL SELECT id + 1 FROM items WHERE id < 9) SELECT id FROM items ORDER BY id";
    private static readonly ResultField[] Fields = [ResultField.Create("id", Codecs.Int64)];
    private static readonly Projection<Item> Projection = Projection<Item>.Create(
        Fields, row => new(row.Read(0, Codecs.Int64)));

    internal static async Task RunIfRequested()
    {
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("A real Native AOT executable is required.");
        var postgres = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set NW_TEST_POSTGRES to an isolated real PostgreSQL cluster.");
        var directory = Path.Combine(Path.GetTempPath(), "korda-ready-scheduling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await Run(DatabaseProvider.Sqlite, Path.Combine(directory, "ready.db"));
            await Run(DatabaseProvider.PostgreSql, postgres);
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task Run(DatabaseProvider provider, string connection)
    {
        var before = _checks;
        await SetupAndReadyOverlap(provider, connection);
        await LateReadyAndNativeTail(provider, connection);
        await RejectedReadyScheduling(provider, connection);
        await CoordinatorTailAndNextGeneration(provider, connection);
        Console.WriteLine($"READY_SCHEDULING_PASS provider={provider} scenarios=4 assertions={_checks - before}; real readiness, deferred scheduling, rejected offers and native/coordinator tails");
    }

    private static DataSource Create(DatabaseProvider provider, string connection)
    {
        var builder = new DataSourceBuilder().ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxBatchRows = 1 });
        if (provider == DatabaseProvider.Sqlite) builder.UseSqlite(connection);
        else builder.UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        return builder.Build();
    }

    private static HostStreamTransport Transport(DataSource source, DataSession session, out uint projectionId, CancellationToken token = default)
    {
        projectionId = source.RegisterProjection(Projection);
        using var plan = SqlSubmission.Create(session, SqlCommand.Create(Sql), 1, projectionId);
        return new(source, NativeHost.CreatePull(session, plan, ReadOnlyMemory<byte>.Empty), 1, token);
    }

    private static Item Read(DataSource source, ScopedStreamBatch batch, uint projectionId)
    {
        var limits = new StreamLimits(1, checked((int)source.Limits.MaxBatchBytes), 10, 1024 * 1024);
        // The built-in, unconfigured Int64 codec keeps its public logical ID.
        var layout = TypedStream<Item>.CreateProjectionLayout(Projection, source.Binding.SemanticProfile,
            projectionId, [Codecs.Int64.Id], limits);
        using var lease = new RowBatchLease(batch, layout.Expectation, Fields, limits);
        Check(lease.Rows == 1 && !lease.End, "Each requested full batch contains its one expected business row.");
        return lease.Materialize(0, Projection);
    }

    private static void Drained(HostPullSnapshot state, ulong generation)
    {
        Check(state.Generation == generation && state.SetupReturned && state.Accepted && state.Ready
            && state.Published && state.Consumed && !state.Active && state.ResultTaken && state.RegistrationDrained
            && !state.Rooted && !state.SlotReserved && state.Controls == 0 && state.CallbackEntries == 0,
            "A consumed generation leaves no callback, registration, root, slot or request obligation.");
    }

    private static async Task SetupAndReadyOverlap(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var scheduler = new RecordingScheduler();
        source.TestPullScheduler = scheduler;
        var transport = Transport(source, session, out var projectionId);
        using var releaseSetup = new ManualResetEventSlim();
        using var releaseReady = new ManualResetEventSlim();
        var setupEntered = NewSignal(); var readyEntered = NewSignal();
        var takes = 0; var publications = 0;
        source.TestPullBoundary = boundary =>
        {
            if (boundary == HostPullBoundary.RequestReturned)
            { setupEntered.TrySetResult(); Hold(releaseSetup, "request setup"); }
            if (boundary == HostPullBoundary.Ready)
            { readyEntered.TrySetResult(); Hold(releaseReady, "managed ready entry"); }
            if (boundary == HostPullBoundary.ResultTaken) Interlocked.Increment(ref takes);
            if (boundary == HostPullBoundary.Published) Interlocked.Increment(ref publications);
        };
        Task<ValueTask<ScopedStreamBatch>>? request = null;
        try
        {
            request = Task.Run(() => transport.FetchAsync());
            await setupEntered.Task.WaitAsync(Bound); await readyEntered.Task.WaitAsync(Bound);
            var held = transport.PullSnapshot;
            Check(held.Generation == 1 && held.Active && held.Accepted && held.Ready && !held.SetupReturned
                && held.Controls == 1 && held.CallbackEntries == 1 && held.Rooted && held.SlotReserved
                && !held.ResultTaken && !held.Published && !request.IsCompleted,
                "Actual readiness cannot publish while request setup and its callback entry are both held.");
            releaseReady.Set();
            await Until(() => transport.PullSnapshot.CallbackEntries == 0, "The ready callback did not drain its last entry.");
            await Until(() => scheduler.Queued == 1, "Early ready did not offer its work item.");
            await Task.Run(scheduler.ExecuteOne).WaitAsync(Bound);
            held = transport.PullSnapshot;
            Check(scheduler.Offers == 1 && scheduler.Executions == 1 && !held.SetupReturned && held.Controls == 1 && held.Rooted && held.SlotReserved
                && !held.ResultTaken && !held.Published && Volatile.Read(ref takes) == 0,
                "Callback exit alone cannot bypass the remaining setup control.");
            releaseSetup.Set();
            var pending = await request.WaitAsync(Bound);
            await Until(() => scheduler.Queued == 1, "Returning setup lost the earlier ready notification.");
            await Task.Run(scheduler.ExecuteOne).WaitAsync(Bound);
            var batch = await pending.AsTask().WaitAsync(Bound);
            Check(Read(source, batch, projectionId).Id == 1, "The first overlapping request materializes its original business row.");
            Drained(transport.PullSnapshot, 1);
            Check(scheduler.Offers == 2 && scheduler.Executions == 2 && takes == 1 && publications == 1, "Overlapping setup/ready notifications take and publish only once.");
        }
        finally
        {
            releaseReady.Set(); releaseSetup.Set();
            source.TestPullBoundary = null; scheduler.ReleaseAll();
            await transport.JoinAsync().WaitAsync(Bound);
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider);
        }
    }

    private static async Task LateReadyAndNativeTail(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var scheduler = new RecordingScheduler();
        source.TestPullScheduler = scheduler;
        var transport = Transport(source, session, out var projectionId);
        var arrived = Enumerable.Range(0, 3).Select(_ => NewSignal()).ToArray();
        var release = Enumerable.Range(0, 3).Select(_ => new ManualResetEventSlim()).ToArray();
        using var releaseOldTail = new ManualResetEventSlim();
        var oldTail = NewSignal();
        using var proxy = new ReadyProxy(source, provider);
        proxy.BeforeManaged = generation =>
        {
            var index = checked((int)generation - 1);
            arrived[index].TrySetResult(); Hold(release[index], "late ready forwarding");
        };
        proxy.AfterManaged = generation =>
        { if (generation == 1) { oldTail.TrySetResult(); Hold(releaseOldTail, "old native callback tail"); } };
        var takes = 0; var publications = 0;
        source.TestPullBoundary = boundary =>
        {
            if (boundary == HostPullBoundary.ResultTaken) Interlocked.Increment(ref takes);
            if (boundary == HostPullBoundary.Published) Interlocked.Increment(ref publications);
        };
        ScopedStreamBatch? previous = null;
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var pending = transport.FetchAsync();
                await arrived[index].Task.WaitAsync(Bound);
                var held = transport.PullSnapshot;
                Check(held.Generation == (ulong)index + 1 && held.SetupReturned && held.Accepted && !held.Ready
                    && held.Rooted && held.SlotReserved && !held.Published && !pending.IsCompleted,
                    "An accepted request retains its obligations while the real ready notification is held.");
                Check(scheduler.Offers == index && scheduler.Executions == index && scheduler.Queued == 0,
                    "A request awaiting native readiness does not prequeue an otherwise idle work item.");
                release[index].Set();
                await Until(() => scheduler.Queued == 1, "Actual readiness did not schedule its retained request.");
                Check(scheduler.Offers == index + 1 && takes == index && publications == index,
                    "One actual ready notification offers exactly one asynchronous drain.");
                await Task.Run(scheduler.ExecuteOne).WaitAsync(Bound);
                var batch = await pending.AsTask().WaitAsync(Bound);
                Check(Read(source, batch, projectionId).Id == index + 1
                    && (previous is null || ReferenceEquals(batch, previous)),
                    "Immediate generation reuse preserves business order and its stable batch owner.");
                previous = batch;
                Drained(transport.PullSnapshot, (ulong)index + 1);
                if (index == 0) await oldTail.Task.WaitAsync(Bound);
                if (index == 1)
                {
                    Check(!proxy.HasReturned(1), "The next generation finishes before the detached older native notification tail returns.");
                    releaseOldTail.Set();
                    await Until(() => proxy.HasReturned(1), "The old native notification tail did not return.");
                }
            }
            Check(scheduler.Offers == 3 && scheduler.Executions == 3 && takes == 3 && publications == 3 && proxy.Requests == 3,
                "Three deliberately late notifications perform three scheduled drains and three distinct result transfers.");
            Console.WriteLine($"READY_SCHEDULING_COUNT provider={provider} generations=3 offers={scheduler.Offers} executions={scheduler.Executions} takes={takes} publications={publications}");
            proxy.ThrowIfFailed();
        }
        finally
        {
            foreach (var gate in release) gate.Set();
            releaseOldTail.Set(); source.TestPullBoundary = null; scheduler.ReleaseAll();
            await transport.JoinAsync().WaitAsync(Bound);
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            foreach (var gate in release) gate.Dispose();
            CheckZero(provider); proxy.ThrowIfFailed();
        }
    }

    private static async Task RejectedReadyScheduling(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var scheduler = new RecordingScheduler { Reject = true };
        source.TestPullScheduler = scheduler;
        using var cancellation = new CancellationTokenSource();
        var transport = Transport(source, session, out var projectionId, cancellation.Token);
        using var releaseReady = new ManualResetEventSlim();
        var arrived = NewSignal();
        using var proxy = new ReadyProxy(source, provider);
        proxy.BeforeManaged = _ => { arrived.TrySetResult(); Hold(releaseReady, "rejected scheduler readiness"); };
        try
        {
            var pending = transport.FetchAsync();
            await arrived.Task.WaitAsync(Bound);
            Check(scheduler.Offers == 0 && !pending.IsCompleted,
                "A cancelable accepted request also waits for readiness without draining its live registration early.");
            releaseReady.Set();
            await Until(() => scheduler.Offers >= 2, "The reserved coordinator slot did not retry a rejected ready offer.");
            var held = transport.PullSnapshot;
            Check(held.Ready && held.Accepted && held.Rooted && held.SlotReserved && !held.Published
                && !held.RegistrationDrained && scheduler.Executions == 0 && !pending.IsCompleted,
                "Rejected ready scheduling preserves the root, retry slot, registration and unconsumed native result.");
            scheduler.Reject = false;
            await Until(() => scheduler.Queued == 1, "Allowing the scheduler did not make the retained ready notification runnable.");
            await Task.Run(scheduler.ExecuteOne).WaitAsync(Bound);
            var batch = await pending.AsTask().WaitAsync(Bound);
            Check(Read(source, batch, projectionId).Id == 1 && scheduler.Executions == 1,
                "The accepted retry drains its registration and materializes the real result exactly once.");
            Drained(transport.PullSnapshot, 1); proxy.ThrowIfFailed();
        }
        finally
        {
            releaseReady.Set(); scheduler.Reject = false; scheduler.ReleaseAll();
            await transport.JoinAsync().WaitAsync(Bound);
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider); proxy.ThrowIfFailed();
        }
    }

    private static async Task CoordinatorTailAndNextGeneration(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var scheduler = new RecordingScheduler(); scheduler.ReleaseAll();
        source.TestPullScheduler = scheduler;
        var transport = Transport(source, session, out var projectionId);
        using var releaseReady = new ManualResetEventSlim();
        using var context = new HoldingContext();
        var firstReady = NewSignal(); var secondReady = NewSignal();
        using var proxy = new ReadyProxy(source, provider);
        proxy.BeforeManaged = generation =>
        { if (generation == 1) { firstReady.TrySetResult(); Hold(releaseReady, "first ready registration"); } };
        proxy.AfterManaged = generation => { if (generation == 2) secondReady.TrySetResult(); };
        var firstConsumed = new TaskCompletionSource<ScopedStreamBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var pending = transport.FetchAsync();
            await firstReady.Task.WaitAsync(Bound);
            Check(scheduler.Offers == 0, "The first request has no coordinator work before actual readiness.");
            var awaiter = pending.GetAwaiter();
            var priorContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                awaiter.UnsafeOnCompleted(() =>
                {
                    try
                    {
                        Check(!ReadyProxy.InManagedReady, "A user continuation remains off the unmanaged ready callback stack.");
                        firstConsumed.TrySetResult(awaiter.GetResult());
                    }
                    catch (Exception error) { firstConsumed.TrySetException(error); }
                });
            }
            finally { SynchronizationContext.SetSynchronizationContext(priorContext); }
            releaseReady.Set();
            await context.Entered.Task.WaitAsync(Bound);
            var first = await firstConsumed.Task.WaitAsync(Bound);
            Check(Read(source, first, projectionId).Id == 1,
                "The first generation is consumed while its completion scheduler still holds the original Execute stack.");
            Drained(transport.PullSnapshot, 1);
            var next = transport.FetchAsync();
            await secondReady.Task.WaitAsync(Bound);
            var held = transport.PullSnapshot;
            Check(held.Generation == 2 && held.SetupReturned && held.Accepted && held.Ready
                && held.Rooted && held.SlotReserved && !held.Published && !held.ResultTaken && !next.IsCompleted
                && scheduler.Offers == 1 && scheduler.Executions == 1,
                "New readiness remains dirty while the old Execute is held after the preceding result was published.");
            context.Release.Set();
            var second = await next.AsTask().WaitAsync(Bound);
            Check(Read(source, second, projectionId).Id == 2 && scheduler.Offers == 2 && scheduler.Executions == 2,
                "The old Execute tail preserves and reschedules the next generation's ready notification once.");
            Drained(transport.PullSnapshot, 2); context.ThrowIfFailed(); proxy.ThrowIfFailed();
        }
        finally
        {
            releaseReady.Set(); context.Release.Set();
            await transport.JoinAsync().WaitAsync(Bound);
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider); context.ThrowIfFailed(); proxy.ThrowIfFailed();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Hold(ManualResetEventSlim release, string name)
    { if (!release.Wait(Bound)) throw new TimeoutException("Did not release " + name); }
    private static async Task Until(Func<bool> condition, string message)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > Bound) throw new TimeoutException(message);
            await Task.Yield();
        }
    }
    private static void Check(bool condition, string message)
    { Interlocked.Increment(ref _checks); if (!condition) throw new InvalidOperationException(message); }
    private static unsafe void CheckZero(DatabaseProvider provider)
    {
        var engine = provider == DatabaseProvider.Sqlite ? SqliteDataSource.Engine : PostgreSqlDataSource.Engine;
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)EngineModuleLoader.ResolveExport(engine, "nw_db_diagnostic_count_v1");
        foreach (var index in new uint[] { 0, 1, 2, 3, 4, 5, 8, 9 })
            Check(count(index) == 0, "A completed ready scheduling scenario leaves no native owner at counter " + index);
    }

    private sealed class RecordingScheduler : ICallbackScheduler
    {
        private readonly object _gate = new();
        private readonly Queue<IThreadPoolWorkItem> _items = new();
        private bool _released, _reject;
        private int _offers, _executions;
        internal int Offers => Volatile.Read(ref _offers);
        internal int Executions => Volatile.Read(ref _executions);
        internal int Queued { get { lock (_gate) return _items.Count; } }
        internal bool Reject { get { lock (_gate) return _reject; } set { lock (_gate) _reject = value; } }
        public bool TryQueue(IThreadPoolWorkItem item)
        {
            Interlocked.Increment(ref _offers);
            lock (_gate)
            {
                if (_reject) return false;
                var work = new RecordedWork(this, item);
                if (!_released) { _items.Enqueue(work); return true; }
                return ThreadPool.UnsafeQueueUserWorkItem(work, preferLocal: false);
            }
        }
        internal void ExecuteOne()
        {
            IThreadPoolWorkItem item;
            lock (_gate) item = _items.Dequeue();
            item.Execute();
        }
        internal void ReleaseAll()
        {
            lock (_gate)
            {
                _released = true;
                while (_items.TryDequeue(out var item))
                    ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false);
            }
        }
        private sealed class RecordedWork(RecordingScheduler owner, IThreadPoolWorkItem item) : IThreadPoolWorkItem
        {
            public void Execute() { Interlocked.Increment(ref owner._executions); item.Execute(); }
        }
    }

    // Post queues the real continuation, then holds the actual MRVTS setter's
    // caller until released. This observes the existing Execute tail without a
    // product hook or a different completion path. Test failures are recorded
    // locally because the preceding result has already been published.
    private sealed class HoldingContext : SynchronizationContext, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = NewSignal();
        internal ManualResetEventSlim Release { get; } = new();
        private Exception? _failure;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static item => item.Callback(item.State),
                (Callback: callback, State: state), preferLocal: false);
            Entered.TrySetResult();
            try { Hold(Release, "old completion scheduler return"); }
            catch (Exception failure) { Interlocked.CompareExchange(ref _failure, failure, null); }
        }
        internal void ThrowIfFailed() { if (Volatile.Read(ref _failure) is { } failure) throw failure; }
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }

    private sealed unsafe class ReadyProxy : IDisposable
    {
        private static ReadyProxy? _active;
        [ThreadStatic] private static bool _inManagedReady;
        private readonly object _gate = new();
        private readonly Dictionary<ulong, NativePullCallbacks> _callbacks = new();
        private readonly HashSet<ulong> _returned = new();
        private readonly DataSource _source;
        private readonly CursorPullCalls _original;
        private readonly GCHandle _root;
        private Exception? _failure;
        private int _requests;
        internal Action<ulong>? BeforeManaged, AfterManaged;
        internal int Requests => Volatile.Read(ref _requests);
        internal static bool InManagedReady => _inManagedReady;

        internal ReadyProxy(DataSource source, DatabaseProvider provider)
        {
            _source = source; _original = source.Binding.Pulls ?? throw new InvalidOperationException("No pull exports.");
            if (Interlocked.CompareExchange(ref _active, this, null) is not null)
                throw new InvalidOperationException("Only one callback-forwarding fixture may run at once.");
            _root = GCHandle.Alloc(this);
            var engine = provider == DatabaseProvider.Sqlite ? SqliteDataSource.Engine : PostgreSqlDataSource.Engine;
            source.Binding.Pulls = new(symbol => symbol == "nw_db_cursor_pull_request_v1"
                ? (nint)(delegate* unmanaged[Cdecl]<nint, ulong, uint, ulong, NativePullCallbacks*, NwErrorInfoV1*, uint>)&Request
                : EngineModuleLoader.ResolveExport(engine, symbol));
        }
        internal bool HasReturned(ulong generation) { lock (_gate) return _returned.Contains(generation); }
        internal void ThrowIfFailed() { lock (_gate) if (_failure is { } error) throw new InvalidOperationException("Actual ready forwarding fixture failed.", error); }
        private void Record(Exception error) { lock (_gate) _failure ??= error; }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static uint Request(nint channel, ulong generation, uint rows, ulong bytes, NativePullCallbacks* callbacks, NwErrorInfoV1* error)
        {
            var proxy = Volatile.Read(ref _active);
            if (proxy is null) return NativeStatus.ContractViolation;
            try
            {
                lock (proxy._gate) proxy._callbacks.Add(generation, *callbacks);
                Interlocked.Increment(ref proxy._requests);
                var forwarding = *callbacks;
                forwarding.Context = GCHandle.ToIntPtr(proxy._root); forwarding.Ready = &Ready;
                return proxy._original.Request(channel, generation, rows, bytes, &forwarding, error);
            }
            catch (Exception failure) { proxy.Record(failure); return NativeStatus.ContractViolation; }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Ready(nint context, ulong generation)
        {
            var proxy = (ReadyProxy?)GCHandle.FromIntPtr(context).Target;
            if (proxy is null) return;
            NativePullCallbacks original;
            lock (proxy._gate) original = proxy._callbacks[generation];
            try { proxy.BeforeManaged?.Invoke(generation); } catch (Exception error) { proxy.Record(error); }
            _inManagedReady = true;
            try { original.Ready(original.Context, generation); }
            finally { _inManagedReady = false; }
            try { proxy.AfterManaged?.Invoke(generation); } catch (Exception error) { proxy.Record(error); }
            lock (proxy._gate) { proxy._callbacks.Remove(generation); proxy._returned.Add(generation); }
        }

        public void Dispose()
        {
            _source.Binding.Pulls = _original;
            lock (_gate) Check(_callbacks.Count == 0, "The forwarding fixture is released only after every real native callback returns.");
            _root.Free();
            Interlocked.CompareExchange(ref _active, null, this);
        }
    }

    private sealed record Item(long Id);
}
