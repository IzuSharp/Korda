using System.Runtime.CompilerServices;
using Korda.Data;
using Korda.Database;
using Korda.Database.Internal;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Interop;
using Korda.Interop.Callbacks;

// Real provider tests for an accepted prepare whose executor has not run yet.
// Rejection is the existing coordinator's retry condition, not query failure.
internal static class WorkerExecutionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly Projection<Item> Projection = Projection<Item>.Create(
        [ResultField.Create("id", Codecs.Int64)], row => new(row.Read(0, Codecs.Int64)));
    private static int _checks;

    internal static async Task RunIfRequested()
    {
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("A real Native AOT executable is required.");
        var postgres = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set NW_TEST_POSTGRES to an isolated real PostgreSQL cluster.");
        var directory = Path.Combine(Path.GetTempPath(), "korda-worker-execution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await Run(DatabaseProvider.Sqlite, Path.Combine(directory, "worker.db"));
            await Run(DatabaseProvider.PostgreSql, postgres);
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task Run(DatabaseProvider provider, string connection)
    {
        var before = _checks;
        await UnrunSettlement(provider, connection, closeSource: false);
        await UnrunSettlement(provider, connection, closeSource: true);
        await ExecutionFailure(provider, connection, HostPullBoundary.BeforeRun);
        await ExecutionFailure(provider, connection, HostPullBoundary.RunReturned);
        Console.WriteLine($"WORKER_EXECUTION_PASS provider={provider} scenarios=4 assertions={_checks - before}; accepted unrun cancellation/close and execution-frame failures");
    }

    private static DataSource Create(DatabaseProvider provider, string connection)
    {
        var builder = new DataSourceBuilder().ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxBatchRows = 1 });
        if (provider == DatabaseProvider.Sqlite) builder.UseSqlite(connection);
        else builder.UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        return builder.Build();
    }

    private static HostStreamTransport Transport(DataSource source, DataSession session, CancellationToken token)
    {
        using var plan = SqlSubmission.Create(session, SqlCommand.Create("SELECT CAST(7 AS BIGINT) AS id"),
            1, source.RegisterProjection(Projection));
        return new(source, NativeHost.CreatePull(session, plan, ReadOnlyMemory<byte>.Empty), 1, token);
    }

    private static async Task UnrunSettlement(DatabaseProvider provider, string connection, bool closeSource)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var scheduler = new RejectingScheduler();
        source.TestPullScheduler = scheduler;
        using var cancellation = new CancellationTokenSource();
        var transport = Transport(source, session, cancellation.Token);
        var sqlBefore = Diagnostic(provider, 6);
        try
        {
            var pending = transport.FetchAsync();
            await Until(() => scheduler.Offers >= 2, "The admitted request did not retain its rejected scheduling offer.");
            var state = transport.PullSnapshot;
            Check(state.Accepted && state.SetupReturned && !state.Ready && state.Rooted && state.SlotReserved
                && !pending.IsCompleted && Diagnostic(provider, 6) == sqlBefore,
                "Prepare returns an accepted asynchronous obligation without executing SQL on the initiating thread.");

            if (closeSource)
            {
                await source.DisposeAsync().AsTask().WaitAsync(Bound);
                Check(source.Cleanup.LocalResourcesReleased,
                    "Source join settles never-run native work even while its managed scheduler rejects every offer.");
            }
            else await cancellation.CancelAsync().WaitAsync(Bound);

            await Until(() => transport.PullSnapshot.Ready, "Close/cancel did not independently settle the native prepared generation.");
            state = transport.PullSnapshot;
            Check(!pending.IsCompleted && state.Accepted && state.Ready && state.Rooted && state.SlotReserved
                && !state.Published && Diagnostic(provider, 6) == sqlBefore,
                "Native settlement retains the managed callback root and retry reservation until its scheduled drain.");
            scheduler.Reject = false;
            Exception? observed = null;
            try { using var unexpected = await pending.AsTask().WaitAsync(Bound); }
            catch (Exception error) when (error is not TimeoutException) { observed = error; }
            Check(observed is not null, "A never-run canceled/closed generation cannot publish a business result.");
            if (!closeSource) Check(observed is OperationCanceledException,
                "Pre-run cancellation retains the original cancellation outcome.");
            Drained(transport.PullSnapshot);
            Check(Diagnostic(provider, 6) == sqlBefore, "A late one-time execution claim after settlement performs no SQL.");
            await transport.JoinAsync().WaitAsync(Bound);
            if (!closeSource)
            {
                source.TestPullScheduler = null;
                Check(await session.ScalarAsync(SqlCommand.Create("SELECT CAST(9 AS BIGINT)"), Codecs.Int64) == 9,
                    "The same real session remains usable after pre-run cancellation.");
            }
        }
        finally
        {
            scheduler.Reject = false; source.TestPullScheduler = null;
            await transport.JoinAsync().WaitAsync(Bound);
            await session.DisposeAsync().AsTask().WaitAsync(Bound);
            await source.DisposeAsync().AsTask().WaitAsync(Bound);
            await Until(() => Zero(provider), "Unrun settlement leaked a native owner.");
        }
    }

    private static async Task ExecutionFailure(DatabaseProvider provider, string connection, HostPullBoundary boundary)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var transport = Transport(source, session, default);
        var original = new ExecutionObserverFailure();
        var sqlBefore = Diagnostic(provider, 6);
        source.TestPullBoundary = value => { if (value == boundary) throw original; };
        try
        {
            Exception? observed = null;
            try { using var unexpected = await transport.FetchAsync().AsTask().WaitAsync(Bound); }
            catch (Exception error) when (error is not TimeoutException) { observed = error; }
            Check(ReferenceEquals(original, observed), "Execution-frame compensation preserves the original managed failure identity.");
            Drained(transport.PullSnapshot);
            Check(Diagnostic(provider, 6) == sqlBefore + (boundary == HostPullBoundary.BeforeRun ? 0UL : 1UL),
                "A failed execution observer neither loses admitted work nor repeats SQL.");
            var retirement = await transport.JoinAsync().WaitAsync(Bound);
            Check(retirement.NativeRetired, "A failed execution frame completes cold retirement before releasing owners.");
        }
        finally
        {
            source.TestPullBoundary = null;
            await transport.JoinAsync().WaitAsync(Bound);
            await session.DisposeAsync().AsTask().WaitAsync(Bound);
            await source.DisposeAsync().AsTask().WaitAsync(Bound);
            await Until(() => Zero(provider), "Execution-frame compensation leaked a native owner.");
        }
    }

    private static void Drained(HostPullSnapshot state) => Check(state.SetupReturned && state.Published && state.Consumed
        && !state.Active && !state.Rooted && !state.SlotReserved && state.RegistrationDrained
        && state.Controls == 0 && state.CallbackEntries == 0,
        "Completion drains registration, native controls, callback entries, root and coordinator reservation exactly once.");

    private static unsafe ulong Diagnostic(DatabaseProvider provider, uint index)
    {
        var engine = provider == DatabaseProvider.Sqlite ? SqliteDataSource.Engine : PostgreSqlDataSource.Engine;
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)EngineModuleLoader.ResolveExport(engine, "nw_db_diagnostic_count_v1");
        return count(index);
    }
    private static bool Zero(DatabaseProvider provider) => new uint[] { 0, 1, 2, 3, 4, 5, 8, 9 }.All(index => Diagnostic(provider, index) == 0);
    private static async Task Until(Func<bool> condition, string failure)
    {
        var until = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException(failure);
            await Task.Delay(10);
        }
    }
    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new InvalidOperationException(message); }
    private sealed record Item(long Id);
    private sealed class ExecutionObserverFailure : Exception;
    private sealed class RejectingScheduler : ICallbackScheduler
    {
        private int _offers;
        internal int Offers => Volatile.Read(ref _offers);
        internal volatile bool Reject = true;
        public bool TryQueue(IThreadPoolWorkItem item)
        {
            Interlocked.Increment(ref _offers);
            return !Reject && ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false);
        }
    }
}
