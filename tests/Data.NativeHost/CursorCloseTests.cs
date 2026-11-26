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

internal static class CursorCloseTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static int _checks, _rejectRequests;

    internal static async Task RunIfRequested(string[] args)
    {
        if (!args.Contains("--cursor-close-only", StringComparer.Ordinal)) return;
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("A real Native AOT executable is required.");
        var postgres = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set NW_TEST_POSTGRES to an isolated real PostgreSQL cluster.");
        var directory = Path.Combine(Path.GetTempPath(), "korda-close-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            BatchResultTests.VerifyRecord();
            CursorCloseRecordTests.Verify();
            await BatchResultTests.VerifyFetches(DatabaseProvider.Sqlite, Path.Combine(directory, "fetches.db"));
            await BatchResultTests.VerifyFetches(DatabaseProvider.PostgreSql, postgres);
            await Run(DatabaseProvider.Sqlite, Path.Combine(directory, "close.db"));
            await Run(DatabaseProvider.PostgreSql, postgres);
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task Run(DatabaseProvider provider, string connection)
    {
        var before = _checks;
        foreach (var boundary in new[]
        {
            HostCloseBoundary.BeforeRoot, HostCloseBoundary.RootAllocated, HostCloseBoundary.BeforeReserve,
            HostCloseBoundary.Reserved, HostCloseBoundary.BeforeRequest, HostCloseBoundary.RequestCalled,
            HostCloseBoundary.RequestReturned, HostCloseBoundary.Ready, HostCloseBoundary.BeforeTake,
            HostCloseBoundary.ResultTaken, HostCloseBoundary.Published
        })
            await SetupFailure(provider, connection, boundary, rejectScheduler: false);
        foreach (var boundary in new[] { HostCloseBoundary.BeforeRoot, HostCloseBoundary.RootAllocated, HostCloseBoundary.BeforeReserve })
            await SetupFailure(provider, connection, boundary, rejectScheduler: true);
        await HeldBoundary(provider, connection, HostCloseBoundary.Ready);
        await HeldBoundary(provider, connection, HostCloseBoundary.RequestReturned);
        await IndeterminateWithLateReady(provider, connection);
        await RejectedThenAcceptedScheduling(provider, connection);
        await ExplicitNativeRejection(provider, connection);
        await ParentAlreadyClosed(provider, connection);
        Console.WriteLine($"CURSOR_CLOSE_PASS provider={provider} assertions={_checks - before}; generation0, setup/callback drains, rejected scheduling, cold fallback and zero native owners");
    }

    private static DataSource Create(DatabaseProvider provider, string connection)
    {
        var builder = new DataSourceBuilder().ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxBatchRows = 1 });
        if (provider == DatabaseProvider.Sqlite) builder.UseSqlite(connection);
        else builder.UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        return builder.Build();
    }

    private static HostStreamTransport CreateTransport(DataSource source, DataSession session)
    {
        var projection = Projection<Item>.Create([ResultField.Create("id", Codecs.Int64)], row => new(row.Read(0, Codecs.Int64)));
        var id = source.RegisterProjection(projection);
        using var plan = SqlSubmission.Create(session,
            SqlCommand.Create("SELECT CAST(1 AS BIGINT) AS id UNION ALL SELECT CAST(2 AS BIGINT) AS id ORDER BY id"), 1, id);
        return new(source, NativeHost.CreatePull(session, plan, ReadOnlyMemory<byte>.Empty), 1, CancellationToken.None);
    }

    private static async Task OpenCursor(HostStreamTransport transport)
    {
        using var batch = await transport.FetchAsync();
        Check(batch.IsActive && !batch.End, "The real cursor has an admitted first batch and remains open before close.");
    }

    private static void CheckDrained(HostCloseSnapshot snapshot)
    {
        Check(snapshot.Generation == 0 && snapshot.SetupReturned && snapshot.Published && snapshot.NativeSafe
            && !snapshot.Rooted && !snapshot.SlotReserved && snapshot.Controls == 0 && snapshot.CallbackEntries == 0,
            "Close publication retains no setup, callback root, slot or native control after proven cleanup.");
    }

    private static async Task SetupFailure(DatabaseProvider provider, string connection, HostCloseBoundary fault, bool rejectScheduler)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        if (rejectScheduler) source.TestCloseScheduler = new RejectAllScheduler();
        var transport = CreateTransport(source, session);
        await OpenCursor(transport);
        var marker = new InvalidDataException("close boundary " + fault);
        var publications = 0;
        source.TestCloseBoundary = boundary =>
        {
            if (boundary == HostCloseBoundary.Published) Interlocked.Increment(ref publications);
            if (boundary == fault) throw marker;
        };
        try
        {
            var first = transport.JoinAsync();
            Check(ReferenceEquals(first, transport.JoinAsync()), "Repeated close joins share the same one-shot completion.");
            var failure = await Failure<InvalidDataException>(first);
            Check(ReferenceEquals(failure, marker), "The first close boundary failure retains exact exception identity: " + fault);
            CheckDrained(transport.CloseSnapshot);
            Check(publications == 1,
                "A close boundary failure must not duplicate publication: " + fault);
            if (rejectScheduler)
                Check(!transport.CloseSnapshot.RequestEntered && transport.CloseSnapshot.FallbackSettled,
                    "An unreserved setup failure drains its cold cleanup without scheduler acceptance.");
            source.TestCloseBoundary = null; source.TestCloseSnapshot = null; source.TestCloseScheduler = null;
            if (fault != HostCloseBoundary.RequestCalled)
                Check(await session.ScalarAsync(SqlCommand.Create("SELECT CAST(7 AS BIGINT)"), Codecs.Int64) == 7,
                    "A definite local close failure preserves same-session reuse after its proven cleanup.");
        }
        finally
        {
            source.TestCloseBoundary = null; source.TestCloseSnapshot = null; source.TestCloseScheduler = null;
            await session.DisposeAsync().AsTask().WaitAsync(Bound);
            await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider);
        }
        Console.WriteLine($"CURSOR_CLOSE_BOUNDARY_PASS provider={provider} boundary={fault} rejectScheduler={rejectScheduler}");
    }

    private static async Task HeldBoundary(DatabaseProvider provider, string connection, HostCloseBoundary heldBoundary)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var transport = CreateTransport(source, session);
        await OpenCursor(transport);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        source.TestCloseBoundary = boundary =>
        {
            if (boundary == HostCloseBoundary.Ready) ready.TrySetResult();
            if (boundary != heldBoundary) return;
            entered.TrySetResult();
            if (!release.Wait(Bound)) throw new TimeoutException("Held close boundary was not released.");
        };
        Task<StreamRetirement>? join = null;
        try
        {
            join = Task.Run(() => transport.JoinAsync());
            await entered.Task.WaitAsync(Bound);
            var held = transport.CloseSnapshot;
            Check(!join.IsCompleted && !held.Published && held.Rooted && held.SlotReserved,
                "A held close boundary cannot publish or release its callback cookie.");
            if (heldBoundary == HostCloseBoundary.Ready)
                Check(held.Ready && held.CallbackEntries == 1,
                    "The held actual generation0 callback keeps its entry obligation live.");
            else
            {
                await ready.Task.WaitAsync(Bound);
                held = transport.CloseSnapshot;
                Check(held.RequestReturned && held.Accepted && !held.SetupReturned && held.Controls == 1 && held.Ready,
                    "An actual ready callback cannot bypass the still-active close request/setup control.");
            }
            release.Set();
            Check((await join.WaitAsync(Bound)).NativeRetired, "The final close returns only after its held boundary drains.");
            CheckDrained(transport.CloseSnapshot);
            Check(!transport.CloseSnapshot.FallbackStarted && transport.CloseSnapshot.ResultTaken,
                "Normal held callback/setup cases still use the direct close receipt.");
            source.TestCloseBoundary = null;
            Check(await session.ScalarAsync(SqlCommand.Create("SELECT CAST(7 AS BIGINT)"), Codecs.Int64) == 7,
                "The same physical session is reusable after direct close completion.");
        }
        finally
        {
            release.Set();
            if (join is not null) await join.WaitAsync(Bound);
            source.TestCloseBoundary = null;
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider);
        }
    }

    private static async Task IndeterminateWithLateReady(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var transport = CreateTransport(source, session);
        await OpenCursor(transport);
        var marker = new InvalidDataException("close request returned before managed acceptance was recorded");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        source.TestCloseBoundary = boundary =>
        {
            if (boundary == HostCloseBoundary.RequestCalled) throw marker;
            if (boundary != HostCloseBoundary.Ready) return;
            ready.TrySetResult();
            if (!release.Wait(Bound)) throw new TimeoutException("Late close ready was not released.");
        };
        Task<StreamRetirement>? join = null;
        Task? parentClose = null;
        try
        {
            join = Task.Run(() => transport.JoinAsync());
            await ready.Task.WaitAsync(Bound);
            await Until(() => transport.CloseSnapshot.SetupReturned, "Unknown close acceptance did not leave its request control.");
            var held = transport.CloseSnapshot;
            Check(held.Indeterminate && held.Rooted && held.SlotReserved && held.CallbackEntries == 1
                && !held.FallbackSettled && !held.NativeSafe && !held.Published && !join.IsCompleted,
                "Unknown close acceptance retains its callback cookie while the late callback entry is held.");
            parentClose = source.DisposeAsync().AsTask();
            Check(!parentClose.IsCompleted && !source.Cleanup.LocalResourcesReleased,
                "Public source close cannot establish native safety while the child's close notification tail is held.");
            release.Set();
            var failure = await Failure<InvalidDataException>(join);
            Check(ReferenceEquals(failure, marker), "Cold cleanup preserves the original unknown-acceptance failure.");
            await parentClose.WaitAsync(Bound);
            CheckDrained(transport.CloseSnapshot);
            Check(transport.CloseSnapshot.FallbackStarted && transport.CloseSnapshot.FallbackSettled
                && source.Cleanup.LocalResourcesReleased,
                "Unknown acceptance and public parent closure both finish only after the notification-tail safety proof.");
        }
        finally
        {
            release.Set(); source.TestCloseBoundary = null;
            if (join is not null) { try { await join.WaitAsync(Bound); } catch (InvalidDataException failure) when (ReferenceEquals(failure, marker)) { } }
            if (parentClose is not null) await parentClose.WaitAsync(Bound);
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider);
        }
    }

    private static async Task RejectedThenAcceptedScheduling(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var scheduler = new PausedScheduler();
        source.TestCloseScheduler = scheduler;
        var transport = CreateTransport(source, session);
        await OpenCursor(transport);
        Task<StreamRetirement>? join = null;
        try
        {
            join = transport.JoinAsync();
            await scheduler.Rejected.Task.WaitAsync(Bound);
            await Until(() => transport.CloseSnapshot.Ready, "The native close did not deliver ready while managed scheduling was rejected.");
            var held = transport.CloseSnapshot;
            Check(!join.IsCompleted && held.Rooted && held.SlotReserved && !held.Published,
                "Rejected scheduling retains the actual callback cookie and retry reservation.");
            scheduler.Allow();
            Check((await join.WaitAsync(Bound)).NativeRetired, "A retained retry slot completes when the scheduler accepts again.");
            CheckDrained(transport.CloseSnapshot);
            Check(!transport.CloseSnapshot.FallbackStarted, "Temporary scheduling refusal does not manufacture a generic close fallback.");
        }
        finally
        {
            scheduler.Allow();
            if (join is not null) await join.WaitAsync(Bound);
            source.TestCloseScheduler = null;
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint RejectClose(nint channel, NativePullCallbacks* callbacks, NwErrorInfoV1* error)
    { Interlocked.Increment(ref _rejectRequests); return NativeStatus.InvalidState; }

    private static unsafe CursorPullCalls RejectingCloseCalls(DatabaseProvider provider)
    {
        var engine = provider == DatabaseProvider.Sqlite ? SqliteDataSource.Engine : PostgreSqlDataSource.Engine;
        return new(symbol => symbol == "nw_db_cursor_pull_close_request_v1"
            ? (nint)(delegate* unmanaged[Cdecl]<nint, NativePullCallbacks*, NwErrorInfoV1*, uint>)&RejectClose
            : EngineModuleLoader.ResolveExport(engine, symbol));
    }

    private static async Task ExplicitNativeRejection(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var transport = CreateTransport(source, session);
        await OpenCursor(transport);
        var original = source.Binding.Pulls;
        source.Binding.Pulls = RejectingCloseCalls(provider);
        var before = Volatile.Read(ref _rejectRequests);
        try
        {
            Check((await transport.JoinAsync().WaitAsync(Bound)).NativeRetired,
                "An explicit close-request rejection uses the preserved cold cleanup contract.");
            var snapshot = transport.CloseSnapshot;
            CheckDrained(snapshot);
            Check(Volatile.Read(ref _rejectRequests) == before + 1 && snapshot.RequestEntered && snapshot.RequestReturned
                && !snapshot.Accepted && !snapshot.Ready && !snapshot.ResultTaken && snapshot.FallbackStarted && snapshot.FallbackSettled,
                "A definitely rejected close request has no callback/take and one settled cold fallback.");
            source.Binding.Pulls = original;
            Check(await session.ScalarAsync(SqlCommand.Create("SELECT CAST(7 AS BIGINT)"), Codecs.Int64) == 7,
                "An explicit close rejection preserves the real session after its cold cleanup succeeds.");
        }
        finally
        {
            source.Binding.Pulls = original;
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider);
        }
    }

    private static async Task ParentAlreadyClosed(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var transport = CreateTransport(source, session);
        await OpenCursor(transport);
        await source.DisposeAsync().AsTask().WaitAsync(Bound);
        Check(source.Cleanup.LocalResourcesReleased, "The parent source actually completed closure before child disposal.");
        try
        {
            Check((await transport.JoinAsync().WaitAsync(Bound)).NativeRetired,
                "Disposing a child after its source closed remains successful through the cold cleanup path.");
            CheckDrained(transport.CloseSnapshot);
            Check(transport.CloseSnapshot.FallbackStarted && transport.CloseSnapshot.FallbackSettled,
                "A closed source/coordinator uses a proven cleanup fallback rather than new reservation admission.");
        }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider);
        }
    }

    private static async Task Until(Func<bool> condition, string failure)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > Bound) throw new TimeoutException(failure);
            await Task.Yield();
        }
    }

    private static async Task<T> Failure<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(Bound); }
        catch (T error) { Check(true, "Observed expected " + typeof(T).Name); return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static unsafe void CheckZero(DatabaseProvider provider)
    {
        var engine = provider == DatabaseProvider.Sqlite ? SqliteDataSource.Engine : PostgreSqlDataSource.Engine;
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)EngineModuleLoader.ResolveExport(engine, "nw_db_diagnostic_count_v1");
        foreach (var index in new uint[] { 0, 1, 2, 3, 4, 5, 8, 9 })
            Check(count(index) == 0, "A joined close leaves no native owner in counter " + index);
    }

    private static void Check(bool condition, string message)
    { Interlocked.Increment(ref _checks); if (!condition) throw new InvalidOperationException(message); }

    private sealed class RejectAllScheduler : ICallbackScheduler
    { public bool TryQueue(IThreadPoolWorkItem item) => false; }

    private sealed class PausedScheduler : ICallbackScheduler
    {
        private int _allowed;
        internal TaskCompletionSource Rejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Allow() => Volatile.Write(ref _allowed, 1);
        public bool TryQueue(IThreadPoolWorkItem item)
        {
            if (Volatile.Read(ref _allowed) == 0) { Rejected.TrySetResult(); return false; }
            return ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false);
        }
    }

    private sealed record Item(long Id);
}
