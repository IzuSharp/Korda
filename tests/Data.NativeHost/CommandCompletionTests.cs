using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Async;
using Korda.Interop.Callbacks;
using Korda.Query;
using Korda.Query.Internal;

// Every request/result below reaches the real provider. The optional proxy
// forwards only the four private exports and holds actual callback boundaries;
// it never manufactures a native result or substitutes a scheduling path.
internal static class CommandCompletionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static int _checks;

    internal static async Task RunIfRequested()
    {
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("A real Native AOT executable is required.");
        var postgres = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set NW_TEST_POSTGRES to an isolated real PostgreSQL cluster.");
        var directory = Path.Combine(Path.GetTempPath(), "korda-command-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await Run(DatabaseProvider.Sqlite, Path.Combine(directory, "command.db"));
            await Run(DatabaseProvider.PostgreSql, postgres);
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task Run(DatabaseProvider provider, string connection)
    {
        var before = _checks;
        await PublicTerminals(provider, connection);
        foreach (var fault in new[]
        {
            HostCommandBoundary.BeforeRoot, HostCommandBoundary.RootAllocated, HostCommandBoundary.BeforeReserve,
            HostCommandBoundary.Reserved, HostCommandBoundary.BeforeRegister, HostCommandBoundary.Registered,
            HostCommandBoundary.BeforeRequest, HostCommandBoundary.RequestCalled, HostCommandBoundary.RequestReturned,
            HostCommandBoundary.Ready, HostCommandBoundary.BeforeTake, HostCommandBoundary.ResultTaken,
            HostCommandBoundary.Published
        }) await BoundaryFailure(provider, connection, fault, rejectScheduler: false);
        foreach (var fault in new[] { HostCommandBoundary.BeforeRoot, HostCommandBoundary.RootAllocated, HostCommandBoundary.BeforeReserve })
            await BoundaryFailure(provider, connection, fault, rejectScheduler: true);
        await SetupAndReadyOverlap(provider, connection);
        await HeldTakeAndLateCancellation(provider, connection);
        await DetachedTailAndParentJoin(provider, connection);
        await UnknownAcceptanceWithLateReady(provider, connection);
        await RejectedScheduler(provider, connection);
        if (provider == DatabaseProvider.Sqlite) await AcceptedSqliteCancellation(connection);
        else await DispatchedPostgresMutation(connection);
        await FrozenProviderError(provider, connection);
        await UnconsumedTransaction(provider, connection, HostCommandBoundary.ResultTaken);
        await UnconsumedTransaction(provider, connection, HostCommandBoundary.Published);
        await UnconsumedTransaction(provider, connection, HostCommandBoundary.ResultTaken, cleanupFault: true);
        await DefiniteNativeRejection(provider, connection);
        await ClosedAdmissionPrecedence(provider, connection);
        Console.WriteLine($"COMMAND_COMPLETION_PASS provider={provider} assertions={_checks - before}; real public terminals, setup/callback/control drains, cancellation, cold owner cleanup and counters0..5/8/9=0");
    }

    private static DataSource Create(DatabaseProvider provider, string connection)
    {
        var builder = new DataSourceBuilder().ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxBatchRows = 1 });
        if (provider == DatabaseProvider.Sqlite) builder.UseSqlite(connection);
        else builder.UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        return builder.Build();
    }
    private static SqlCommand Mutation(string text) => SqlCommand.Create(text, kind: SqlCommandKind.Mutation);
    private static string TableName() => "command_" + Guid.NewGuid().ToString("N");
    private static DatabaseOwner Plan(DataSession session, string? sql = null)
        => SqlSubmission.Create(session, Mutation(sql ?? "CREATE TABLE " + TableName() + " (id BIGINT)"), 0, 0);
    private static HostCommandController<long> Controller(DataSource source, CancellationToken token = default)
        => new(source, NativeCommandKind.Execute, 100, NativeCommand.Count, token);
    private static Task<long> Request(HostCommandController<long> controller, DataSession session, DatabaseOwner plan)
        => controller.Request(session.Owner, plan, default, 0);

    private static void Drained(HostCommandSnapshot snapshot)
    {
        Check(snapshot.SetupReturned && snapshot.Published && snapshot.NativeSafe && snapshot.RegistrationDrained
            && !snapshot.Rooted && !snapshot.SlotReserved && snapshot.Controls == 0 && snapshot.CallbackEntries == 0,
            "A published command has drained every local setup, callback, cancellation, root and slot obligation.");
    }

    private static async Task PublicTerminals(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        var seen = new ConcurrentDictionary<uint, int>();
        var retirements = 0;
        source.TestRetirement = _ => Interlocked.Increment(ref retirements);
        source.TestCommandSnapshot = snapshot =>
        {
            Drained(snapshot);
            Check(snapshot.Accepted && snapshot.Ready && snapshot.ResultTaken && !snapshot.FallbackStarted,
                "A normal public terminal transfers its typed result through direct command readiness.");
            seen.AddOrUpdate(snapshot.RequestKind, 1, (_, count) => count + 1);
        };
        using var proxy = new CommandProxy(source, provider);
        var name = TableName();
        var builder = new TableBuilder<Item>(name);
        var id = builder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
        var table = builder.Build(row => new Item(row.Get(id)), (row, item) => row.Set(id, item.Id));
        try
        {
            await session.ExecuteAsync(Mutation("CREATE TABLE " + name + " (\"Id\" BIGINT PRIMARY KEY)"));
            Console.WriteLine($"COMMAND_PUBLIC_STAGE provider={provider} stage=empty-count");
            using (var empty = session.From(table))
                Check(await empty.CountAsync() == 0, "Legal one-row batches support empty public Count.");
            Console.WriteLine($"COMMAND_PUBLIC_STAGE provider={provider} stage=empty-any");
            using (var empty = session.From(table))
                Check(!await empty.AnyAsync(), "Legal one-row batches support empty public Any.");
            Console.WriteLine($"COMMAND_PUBLIC_STAGE provider={provider} stage=crud");
            Check(await session.InsertAsync(table, new Item(1)) == 1 && await session.InsertAsync(table, new Item(2)) == 1,
                "Public nonreturning CRUD transfers its actual affected-row count.");
            Console.WriteLine($"COMMAND_PUBLIC_STAGE provider={provider} stage=populated-count-any");
            using (var query = session.From(table))
                Check(await query.CountAsync() == 2, "Public Count uses direct scalar reading with MaxBatchRows=1.");
            using (var query = session.From(table))
                Check(await query.AnyAsync(), "Public Any uses direct scalar reading with MaxBatchRows=1.");
            Console.WriteLine($"COMMAND_PUBLIC_STAGE provider={provider} stage=transactions");
            await using (var transaction = await session.BeginTransactionAsync())
            {
                await session.InsertAsync(table, new Item(3));
                await using (var savepoint = await transaction.CreateSavepointAsync())
                {
                    await session.InsertAsync(table, new Item(4));
                    await savepoint.RollbackAsync();
                    await savepoint.ReleaseAsync();
                }
                await transaction.CommitAsync();
                Check(transaction.Outcome == TransactionOutcome.Committed, "Direct transaction completion retains confirmed Commit.");
            }
            await using (var transaction = await session.BeginTransactionAsync())
            {
                await session.InsertAsync(table, new Item(5));
                await transaction.RollbackAsync();
                Check(transaction.Outcome == TransactionOutcome.RolledBack, "Direct transaction completion retains confirmed Rollback.");
            }
            using (var query = session.From(table)) Check(await query.CountAsync() == 3, "Savepoint and transaction effects remain exact.");
            await session.ExecuteAsync(Mutation("DROP TABLE " + name));
            await session.DisposeAsync();
            Check(session.Cleanup.LocalResourcesReleased, "Direct child close reports real local safety.");
            Check(Enumerable.Range(1, 7).All(kind => seen.ContainsKey((uint)kind)) && retirements == 0,
                "All seven public command kinds use the new path without a normal generic operation retirement.");
            Check(proxy.Requests == proxy.Readies && proxy.Takes == proxy.Requests && proxy.Errors == 0,
                "Each successful public command has exactly one actual request, ready notification and typed take.");
        }
        finally
        {
            source.TestCommandSnapshot = null; source.TestRetirement = null;
            await session.DisposeAsync().AsTask().WaitAsync(Bound);
            await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider); proxy.ThrowIfFailed();
        }
    }

    private static async Task BoundaryFailure(DatabaseProvider provider, string connection, HostCommandBoundary fault, bool rejectScheduler)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        using var plan = Plan(session);
        using var cancellation = new CancellationTokenSource();
        if (rejectScheduler) source.TestCommandScheduler = new RejectAllScheduler();
        var controller = Controller(source, cancellation.Token);
        var marker = new InvalidDataException("command boundary " + fault);
        var fired = 0; var publications = 0;
        source.TestCommandBoundary = boundary =>
        {
            if (boundary == HostCommandBoundary.Published) Interlocked.Increment(ref publications);
            if (boundary == fault && Interlocked.CompareExchange(ref fired, 1, 0) == 0) throw marker;
        };
        try
        {
            var failure = await Failure<InvalidDataException>(Request(controller, session, plan));
            Check(ReferenceEquals(failure, marker), "The original command boundary error retains exact identity: " + fault);
            Drained(controller.Snapshot);
            Check(publications == 1, "A failed one-shot command publishes its original result once.");
            if (rejectScheduler) Check(!controller.Snapshot.RequestEntered,
                "A pre-reservation failure finishes without requiring an accepted scheduler offer.");
        }
        finally
        {
            source.TestCommandBoundary = null; source.TestCommandScheduler = null;
            plan.Dispose();
            await session.DisposeAsync().AsTask().WaitAsync(Bound);
            await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider);
        }
    }

    private static async Task SetupAndReadyOverlap(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection);
        var session = await source.OpenSessionAsync();
        using var plan = Plan(session);
        var controller = Controller(source);
        var setup = Signal(); var ready = Signal();
        using var releaseSetup = new ManualResetEventSlim();
        using var releaseReady = new ManualResetEventSlim();
        var takes = 0; var publications = 0;
        source.TestCommandBoundary = boundary =>
        {
            if (boundary == HostCommandBoundary.RequestReturned) { setup.TrySetResult(); Hold(releaseSetup, "command request setup"); }
            if (boundary == HostCommandBoundary.Ready) { ready.TrySetResult(); Hold(releaseReady, "command ready entry"); }
            if (boundary == HostCommandBoundary.ResultTaken) Interlocked.Increment(ref takes);
            if (boundary == HostCommandBoundary.Published) Interlocked.Increment(ref publications);
        };
        Task<long>? pending = null;
        try
        {
            pending = Task.Run(() => Request(controller, session, plan));
            await setup.Task.WaitAsync(Bound); await ready.Task.WaitAsync(Bound);
            var held = controller.Snapshot;
            Check(held.Accepted && held.Ready && !held.SetupReturned && held.Controls == 1 && held.CallbackEntries == 1
                && held.Rooted && held.SlotReserved && !held.ResultTaken && !pending.IsCompleted,
                "Early ready cannot bypass still-active request and callback entries.");
            releaseReady.Set();
            await Until(() => controller.Snapshot.CallbackEntries == 0, "Actual ready entry did not drain.");
            Check(takes == 0 && publications == 0 && !pending.IsCompleted, "Drained callback alone cannot publish before setup returns.");
            releaseSetup.Set();
            Check(await pending.WaitAsync(Bound) == 0, "The overlapping real DDL result remains exact.");
            Drained(controller.Snapshot);
            Check(takes == 1 && publications == 1, "Early ready is neither lost nor transferred twice.");
        }
        finally
        {
            releaseSetup.Set(); releaseReady.Set(); source.TestCommandBoundary = null;
            if (pending is not null) await pending.WaitAsync(Bound);
            plan.Dispose(); await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider);
        }
    }

    private static async Task HeldTakeAndLateCancellation(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection); var session = await source.OpenSessionAsync();
        using var plan = Plan(session); using var cancellation = new CancellationTokenSource();
        var controller = Controller(source, cancellation.Token);
        var entered = Signal(); using var release = new ManualResetEventSlim();
        var controls = 0;
        source.TestCommandBoundary = boundary =>
        {
            if (boundary == HostCommandBoundary.BeforeTake) { entered.TrySetResult(); Hold(release, "typed take"); }
            if (boundary == HostCommandBoundary.CancelEntered) Interlocked.Increment(ref controls);
        };
        Task<long>? pending = null;
        try
        {
            pending = Request(controller, session, plan);
            await entered.Task.WaitAsync(Bound);
            var held = controller.Snapshot;
            Check(held.SetupReturned && held.Ready && held.Controls == 1 && held.CallbackEntries == 0
                && held.RegistrationDrained && held.Rooted && held.SlotReserved && !held.Published,
                "Typed take retains its local control while the cancellation registration is already drained.");
            cancellation.Cancel();
            Check(controls == 0 && !pending.IsCompleted, "Late cancellation cannot replace a frozen successful result or overlap its take.");
            release.Set(); Check(await pending.WaitAsync(Bound) == 0, "Held take returns its original native result."); Drained(controller.Snapshot);
        }
        finally
        {
            release.Set(); source.TestCommandBoundary = null;
            if (pending is not null) await pending.WaitAsync(Bound);
            plan.Dispose(); await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound); CheckZero(provider);
        }
    }

    private static async Task DetachedTailAndParentJoin(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection); var session = await source.OpenSessionAsync();
        using var plan = Plan(session); var controller = Controller(source);
        using var proxy = new CommandProxy(source, provider);
        using var releaseReady = new ManualResetEventSlim(); using var releaseTail = new ManualResetEventSlim();
        var ready = Signal(); var tail = Signal();
        proxy.BeforeManaged = kind => { if (kind == NativeCommandKind.Execute) { ready.TrySetResult(); Hold(releaseReady, "real ready forwarding"); } };
        proxy.AfterManaged = kind => { if (kind == NativeCommandKind.Execute) { tail.TrySetResult(); Hold(releaseTail, "detached native notification tail"); } };
        Task<long>? pending = null; Task? parent = null;
        try
        {
            pending = Request(controller, session, plan);
            await ready.Task.WaitAsync(Bound);
            Check(controller.Snapshot is { Accepted: true, SetupReturned: true, Ready: false, Rooted: true, SlotReserved: true }
                && !pending.IsCompleted, "Accepted work waits for its actual ready notification.");
            var synchronousConsumer = Task.Run(() =>
            {
                var result = pending.GetAwaiter().GetResult();
                Check(!CommandProxy.InManagedReady, "Synchronous GetResult consumers never execute on the native ready stack.");
                return result;
            });
            releaseReady.Set(); await tail.Task.WaitAsync(Bound);
            Check(await synchronousConsumer.WaitAsync(Bound) == 0, "A consumer can finish while the independent notification tail remains held.");
            Drained(controller.Snapshot);
            Check(Diagnostic(provider, 9) == 1, "The released command handle cannot hide its one remaining native notification lifetime.");
            plan.Dispose(); parent = source.DisposeAsync().AsTask();
            Check(!parent.IsCompleted, "Parent disposal must wait for the actual held notification tail.");
            releaseTail.Set(); await parent.WaitAsync(Bound);
            await session.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider); proxy.ThrowIfFailed();
        }
        finally
        {
            releaseReady.Set(); releaseTail.Set();
            if (pending is not null) await pending.WaitAsync(Bound);
            plan.Dispose(); await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider); proxy.ThrowIfFailed();
        }
    }

    private static async Task UnknownAcceptanceWithLateReady(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection); var session = await source.OpenSessionAsync();
        using var plan = Plan(session); var controller = Controller(source);
        using var proxy = new CommandProxy(source, provider);
        var ready = Signal(); using var release = new ManualResetEventSlim();
        var marker = new InvalidDataException("unknown command acceptance");
        source.TestCommandBoundary = boundary => { if (boundary == HostCommandBoundary.RequestCalled) throw marker; };
        proxy.BeforeManaged = _ => { ready.TrySetResult(); Hold(release, "late ready under unknown acceptance"); };
        Task<long>? pending = null;
        try
        {
            pending = Request(controller, session, plan);
            await ready.Task.WaitAsync(Bound);
            var held = controller.Snapshot;
            Check(held.Indeterminate && held.FallbackStarted && !held.NativeSafe && held.Rooted && held.SlotReserved
                && !held.Published && !pending.IsCompleted,
                "An unknown request result cannot abandon its actual late callback cookie.");
            release.Set();
            Check(ReferenceEquals(await Failure<InvalidDataException>(pending), marker), "Cold proof preserves the unknown-acceptance primary exception.");
            Drained(controller.Snapshot);
            Check(controller.Snapshot.FallbackSettled, "Unknown acceptance is released only after an actual cold join proof.");
        }
        finally
        {
            release.Set(); source.TestCommandBoundary = null;
            if (pending is not null) { try { await pending.WaitAsync(Bound); } catch (InvalidDataException) { } }
            plan.Dispose(); await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider); proxy.ThrowIfFailed();
        }
    }

    private static async Task RejectedScheduler(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection); var session = await source.OpenSessionAsync();
        using var plan = Plan(session); using var cancellation = new CancellationTokenSource();
        var scheduler = new RejectUntilReleasedScheduler(); source.TestCommandScheduler = scheduler;
        var controller = Controller(source, cancellation.Token);
        try
        {
            var pending = Request(controller, session, plan);
            await Until(() => scheduler.Offers >= 2 && controller.Snapshot.Ready, "Rejected ready scheduling was not retried by the retained coordinator.");
            Check(!pending.IsCompleted && controller.Snapshot.Rooted && controller.Snapshot.SlotReserved,
                "Rejected scheduler offers preserve the callback root and slot until actual progress.");
            scheduler.Allow = true;
            Check(await pending.WaitAsync(Bound) == 0, "A later accepted offer completes the original result.");
            Drained(controller.Snapshot);
        }
        finally
        {
            scheduler.Allow = true; source.TestCommandScheduler = null;
            plan.Dispose(); await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound); CheckZero(provider);
        }
    }

    private static async Task AcceptedSqliteCancellation(string connection)
    {
        const DatabaseProvider provider = DatabaseProvider.Sqlite;
        var source = Create(provider, connection); var session = await source.OpenSessionAsync(); var name = TableName();
        await session.ExecuteAsync(Mutation("CREATE TABLE " + name + " (id BIGINT)"));
        var sql = "INSERT INTO " + name + " WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<100000000) SELECT SUM(x) FROM n";
        using var plan = Plan(session, sql); using var cancellation = new CancellationTokenSource();
        var controller = Controller(source, cancellation.Token);
        var setup = Signal(); using var release = new ManualResetEventSlim(); var cancels = 0;
        source.TestCommandBoundary = boundary =>
        {
            if (boundary == HostCommandBoundary.RequestReturned) { setup.TrySetResult(); Hold(release, "cancel-before-setup-return"); }
            if (boundary == HostCommandBoundary.CancelEntered) Interlocked.Increment(ref cancels);
        };
        Task<long>? pending = null;
        try
        {
            pending = Task.Run(() => Request(controller, session, plan));
            await setup.Task.WaitAsync(Bound);
            Check(controller.Snapshot is { Accepted: true, Ready: false, SetupReturned: false, Controls: 1 },
                "The real long command is accepted and still pending before setup returns.");
            cancellation.Cancel(); release.Set();
            var failure = await Failure<OperationCanceledException>(pending);
            Check(failure.CancellationToken == cancellation.Token && pending.IsCanceled && cancels == 1,
                "Accepted cancellation preserves its exact token and sends one real cancel control.");
            Drained(controller.Snapshot); source.TestCommandBoundary = null;
            Check(await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM " + name), Codecs.Int64) == 0,
                "Canceled accepted work has no committed side effect and leaves a reusable session.");
        }
        finally
        {
            release.Set(); source.TestCommandBoundary = null;
            if (pending is not null) { try { await pending.WaitAsync(Bound); } catch (OperationCanceledException) { } }
            plan.Dispose(); await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound); CheckZero(provider);
        }
    }

    private static async Task DispatchedPostgresMutation(string connection)
    {
        const DatabaseProvider provider = DatabaseProvider.PostgreSql;
        var source = Create(provider, connection); var session = await source.OpenSessionAsync();
        var blockerSource = Create(provider, connection); var blocker = await blockerSource.OpenSessionAsync();
        var name = TableName();
        await session.ExecuteAsync(Mutation("CREATE TABLE " + name + " (id BIGINT PRIMARY KEY, v BIGINT NOT NULL)"));
        await session.ExecuteAsync(Mutation("INSERT INTO " + name + " VALUES (1,0)"));
        var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(Mutation("UPDATE " + name + " SET v=1 WHERE id=1"));
        using var plan = Plan(session, "UPDATE " + name + " SET v=2 WHERE id=1");
        using var cancellation = new CancellationTokenSource(); var controller = Controller(source, cancellation.Token);
        var cancels = 0;
        source.TestCommandBoundary = boundary => { if (boundary == HostCommandBoundary.CancelEntered) Interlocked.Increment(ref cancels); };
        Task<long>? pending = null;
        try
        {
            pending = Request(controller, session, plan);
            var observation = SqlCommand.Create("SELECT COUNT(*) FROM pg_stat_activity WHERE state='active' AND wait_event_type='Lock' AND query LIKE '%" + name + "%'");
            var deadline = DateTime.UtcNow + Bound;
            while (await blocker.ScalarAsync(observation, Codecs.Int64) != 1)
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("The real UPDATE did not reach its independently held PostgreSQL row lock.");
                await Task.Delay(1);
            }
            cancellation.Cancel();
            Check(cancels == 1 && !pending.IsCompleted && controller.Snapshot is { Accepted: true, Ready: false, Published: false, Rooted: true, SlotReserved: true },
                "Canceling an actually dispatched PostgreSQL mutation cannot publish an assumed rollback while the provider is still blocked.");
            Check(await blocker.ScalarAsync(observation, Codecs.Int64) == 1,
                "An independent connection still observes the actual mutation waiting for its row lock after cancellation intent.");
            await transaction.RollbackAsync();
            Check(await pending.WaitAsync(Bound) == 1 && !pending.IsCanceled,
                "Dispatched PostgreSQL nonquery cancellation preserves the confirmed original affected count.");
            Drained(controller.Snapshot); source.TestCommandBoundary = null;
            Check(await session.ScalarAsync(SqlCommand.Create("SELECT v FROM " + name + " WHERE id=1"), Codecs.Int64) == 2,
                "The original mutation completed once and its physical session is safely reusable.");
            await session.ExecuteAsync(Mutation("DROP TABLE " + name));
        }
        finally
        {
            source.TestCommandBoundary = null;
            await transaction.DisposeAsync().AsTask().WaitAsync(Bound);
            if (pending is not null) { try { await pending.WaitAsync(Bound); } catch (OperationCanceledException) { } }
            plan.Dispose();
            await blocker.DisposeAsync().AsTask().WaitAsync(Bound); await blockerSource.DisposeAsync().AsTask().WaitAsync(Bound);
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound); CheckZero(provider);
        }
    }

    private static async Task FrozenProviderError(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection); var session = await source.OpenSessionAsync();
        using var plan = Plan(session, "INSERT INTO " + TableName() + " VALUES (1)");
        using var cancellation = new CancellationTokenSource(); var controller = Controller(source, cancellation.Token);
        using var proxy = new CommandProxy(source, provider); var ready = Signal(); using var release = new ManualResetEventSlim();
        proxy.BeforeManaged = _ => { ready.TrySetResult(); Hold(release, "frozen actual provider failure"); };
        Task<long>? pending = null;
        try
        {
            pending = Request(controller, session, plan); await ready.Task.WaitAsync(Bound);
            cancellation.Cancel(); release.Set();
            var failure = await Failure<DatabaseException>(pending);
            Check(failure.Kind == DatabaseErrorKind.Provider && failure.ProviderCode is not null && failure.CorrelationId != 0
                && proxy.Cancels == 1 && proxy.Errors == 1,
                "A late cancellation cannot replace the original frozen provider code/correlation or consume rich detail twice.");
            Drained(controller.Snapshot);
        }
        finally
        {
            release.Set();
            if (pending is not null) { try { await pending.WaitAsync(Bound); } catch (DatabaseException) { } }
            plan.Dispose(); await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider); proxy.ThrowIfFailed();
        }
    }

    private static async Task UnconsumedTransaction(DatabaseProvider provider, string connection, HostCommandBoundary boundary, bool cleanupFault = false)
    {
        var source = Create(provider, connection); var session = await source.OpenSessionAsync();
        using var proxy = new CommandProxy(source, provider);
        var marker = new InvalidDataException("abandon transferred transaction " + boundary);
        var secondary = new IOException("first attempt to clean a transferred transaction failed before dispatch");
        var once = 0; var commands = 0;
        var cold = Signal(); using var release = new ManualResetEventSlim();
        source.TestCommandBoundary = current =>
        {
            if (current == HostCommandBoundary.BeforeRoot) Interlocked.Increment(ref commands);
            if (current == boundary && Interlocked.CompareExchange(ref once, 1, 0) == 0) throw marker;
            if (cleanupFault && commands == 2 && current == HostCommandBoundary.BeforeRequest) throw secondary;
        };
        if (cleanupFault) source.TestBoundary = current =>
        { if (current == AsyncBoundary.BeforeStart) { cold.TrySetResult(); Hold(release, "real cold retry of an untaken new transaction"); } };
        Task<DataTransaction>? pending = null;
        try
        {
            pending = session.BeginTransactionAsync();
            if (cleanupFault)
            {
                await cold.Task.WaitAsync(Bound);
                Check(!pending.IsCompleted && proxy.TransferredOwner != 0 && !OwnerCleanup(source, proxy.TransferredOwner).LocalResourcesReleased,
                    "A failed cleanup attempt retains the exact transferred transaction and keeps the original Task pending until real retirement.");
                release.Set();
            }
            Check(ReferenceEquals(await Failure<InvalidDataException>(pending), marker),
                "A managed result failure retains its primary exception after cleanup of a newly transferred transaction.");
            source.TestCommandBoundary = null; source.TestBoundary = null;
            if (cleanupFault)
                Check(ReferenceEquals(marker.Data["Korda.Database.CommandResultCleanup.v1"], secondary),
                    "The original result error preserves the secondary cleanup failure after a successful cold retry.");
            else
            {
                await using var next = await session.BeginTransactionAsync();
                await next.RollbackAsync();
                Check(next.Outcome == TransactionOutcome.RolledBack, "The unconsumed new transaction was really rolled back before session reuse.");
            }
        }
        finally
        {
            release.Set(); source.TestCommandBoundary = null; source.TestBoundary = null;
            if (pending is not null) { try { await pending.WaitAsync(Bound); } catch (InvalidDataException) { } }
            await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound); CheckZero(provider);
            proxy.ThrowIfFailed();
        }
    }

    private static unsafe DatabaseCloseResult OwnerCleanup(DataSource source, nint owner)
    {
        nint pointer = 0; var error = BindingChecks.Error();
        HostFailure.Check(source.Binding, source.Binding.Calls.nw_db_snapshot_v1(owner, default, &pointer, &error), error);
        using var buffer = new Korda.Database.NativeBuffer(source.Binding, pointer);
        return buffer.ReadSnapshot().Cleanup;
    }

    private static async Task DefiniteNativeRejection(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection); var session = await source.OpenSessionAsync();
        var projection = Projection<Item>.Create([ResultField.Create("id", Codecs.Int64)], row => new(row.Read(0, Codecs.Int64)));
        var id = source.RegisterProjection(projection);
        using var plan = SqlSubmission.Create(session, SqlCommand.Create("SELECT CAST(1 AS BIGINT) AS id"), 3, id);
        var controller = Controller(source); using var proxy = new CommandProxy(source, provider);
        try
        {
            await Failure<ArgumentException>(Request(controller, session, plan));
            Drained(controller.Snapshot);
            Check(controller.DefinitelyRejected && !controller.Snapshot.Accepted && !controller.Snapshot.Ready
                && !controller.Snapshot.FallbackStarted && proxy.Requests == 1 && proxy.Readies == 0 && proxy.Takes == 0,
                "The actual row-plan rejection owes no callback, does not take a result and needs no cold proof.");
            Check(await session.ScalarAsync(SqlCommand.Create("SELECT CAST(7 AS BIGINT)"), Codecs.Int64) == 7,
                "A definite native request rejection leaves the existing physical session reusable.");
        }
        finally
        {
            plan.Dispose(); await session.DisposeAsync().AsTask().WaitAsync(Bound); await source.DisposeAsync().AsTask().WaitAsync(Bound);
            CheckZero(provider); proxy.ThrowIfFailed();
        }
    }

    private static async Task ClosedAdmissionPrecedence(DatabaseProvider provider, string connection)
    {
        var source = Create(provider, connection); var session = await source.OpenSessionAsync();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var request = Mutation("CREATE TABLE " + TableName() + " (id BIGINT)");
        var calls = 0; source.TestCommandBoundary = boundary => { if (boundary == HostCommandBoundary.BeforeRoot) Interlocked.Increment(ref calls); };
        var failure = await Failure<OperationCanceledException>(session.ExecuteAsync(request, canceled.Token));
        Check(failure.CancellationToken == canceled.Token && calls == 0, "Pre-canceled open admission allocates no command controller.");
        await session.DisposeAsync();
        await Failure<ObjectDisposedException>(() => session.ExecuteAsync(request, canceled.Token));
        var another = await source.OpenSessionAsync(); await source.DisposeAsync();
        await Failure<ObjectDisposedException>(() => another.ExecuteAsync(request, canceled.Token));
        await another.DisposeAsync();
        CheckZero(provider);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Hold(ManualResetEventSlim gate, string boundary)
    { if (!gate.Wait(Bound)) throw new TimeoutException("Held command boundary timed out: " + boundary); }
    private static async Task Until(Func<bool> predicate, string failure)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(failure); await Task.Delay(1); }
    }
    private static async Task<T> Failure<T>(Task task) where T : Exception
    { try { await task.WaitAsync(Bound); } catch (T failure) { Interlocked.Increment(ref _checks); return failure; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> Failure<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Bound); } catch (T failure) { Interlocked.Increment(ref _checks); return failure; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message)
    { Interlocked.Increment(ref _checks); if (!value) throw new InvalidOperationException(message); }
    private static unsafe ulong Diagnostic(DatabaseProvider provider, uint kind)
    {
        var engine = provider == DatabaseProvider.Sqlite ? SqliteDataSource.Engine : PostgreSqlDataSource.Engine;
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)EngineModuleLoader.ResolveExport(engine, "nw_db_diagnostic_count_v1");
        return count(kind);
    }
    private static void CheckZero(DatabaseProvider provider)
    { foreach (var kind in new uint[] { 0, 1, 2, 3, 4, 5, 8, 9 }) Check(Diagnostic(provider, kind) == 0, "Command test retained native owner kind " + kind); }
    private sealed record Item(long Id);
    private sealed class RejectAllScheduler : ICallbackScheduler
    { public bool TryQueue(IThreadPoolWorkItem item) => false; }
    private sealed class RejectUntilReleasedScheduler : ICallbackScheduler
    {
        private int _offers;
        internal volatile bool Allow;
        internal int Offers => Volatile.Read(ref _offers);
        public bool TryQueue(IThreadPoolWorkItem item)
        { Interlocked.Increment(ref _offers); return Allow && ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false); }
    }

    private sealed unsafe class CommandProxy : IDisposable
    {
        private static CommandProxy? _active;
        [ThreadStatic] private static bool _inManagedReady;
        private readonly object _gate = new();
        private readonly DataSource _source;
        private readonly CommandCompletionCalls _original;
        private readonly Dictionary<nint, Entry> _callbacks = new();
        private Exception? _failure;
        private int _requests, _readies, _takes, _errors, _cancels;
        private nint _transferredOwner;
        internal Action<NativeCommandKind>? BeforeManaged, AfterManaged;
        internal static bool InManagedReady => _inManagedReady;
        internal int Requests => Volatile.Read(ref _requests);
        internal int Readies => Volatile.Read(ref _readies);
        internal int Takes => Volatile.Read(ref _takes);
        internal int Errors => Volatile.Read(ref _errors);
        internal int Cancels => Volatile.Read(ref _cancels);
        internal nint TransferredOwner => Volatile.Read(ref _transferredOwner);
        private sealed class Entry(CommandProxy owner, NativePullCallbacks callbacks, NativeCommandKind kind)
        {
            internal readonly CommandProxy Owner = owner;
            internal readonly NativePullCallbacks Callbacks = callbacks;
            internal readonly NativeCommandKind Kind = kind;
            internal GCHandle Root;
        }

        internal CommandProxy(DataSource source, DatabaseProvider provider)
        {
            _source = source;
            _original = source.Binding.Completions ?? throw new InvalidOperationException("No command completion exports.");
            if (Interlocked.CompareExchange(ref _active, this, null) is not null)
                throw new InvalidOperationException("One command forwarding fixture may run at a time.");
            source.Binding.Completions = new(symbol => symbol switch
            {
                "nw_db_command_request_v1" => (nint)(delegate* unmanaged[Cdecl]<NativeCommandRequest*, NativePullCallbacks*, nint*, NwErrorInfoV1*, uint>)&RequestNative,
                "nw_db_command_take_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, uint, NativeCommandResult*, NwErrorInfoV1*, uint>)&TakeNative,
                "nw_db_command_take_error_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, nint*, NwErrorInfoV1*, uint>)&ErrorNative,
                "nw_db_command_cancel_v1" => (nint)(delegate* unmanaged[Cdecl]<nint, uint>)&CancelNative,
                _ => throw new InvalidOperationException("Unexpected command export: " + symbol)
            });
        }
        private void Record(Exception error) { lock (_gate) _failure ??= error; }
        internal void ThrowIfFailed()
        { lock (_gate) if (_failure is { } failure) throw new InvalidOperationException("Real command callback forwarding failed.", failure); }
        private void Release(nint context)
        {
            Entry? entry;
            lock (_gate) _callbacks.Remove(context, out entry);
            entry?.Root.Free();
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static uint RequestNative(NativeCommandRequest* request, NativePullCallbacks* callbacks, nint* owner, NwErrorInfoV1* error)
        {
            var proxy = Volatile.Read(ref _active);
            if (proxy is null) return NativeStatus.ContractViolation;
            nint context = 0;
            try
            {
                var entry = new Entry(proxy, *callbacks, request->Kind);
                entry.Root = GCHandle.Alloc(entry); context = GCHandle.ToIntPtr(entry.Root);
                lock (proxy._gate) proxy._callbacks.Add(context, entry);
                var forwarding = *callbacks; forwarding.Context = context; forwarding.Ready = &ReadyNative;
                Interlocked.Increment(ref proxy._requests);
                var status = proxy._original.Request(request, &forwarding, owner, error);
                if (status != NativeStatus.Ok) proxy.Release(context);
                return status;
            }
            catch (Exception failure)
            {
                proxy.Record(failure);
                // No test callback ever throws across this boundary. Retain an
                // unknown accepted cookie for the fixture's explicit failure.
                if (owner != null && *owner == 0 && context != 0) proxy.Release(context);
                return NativeStatus.ContractViolation;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void ReadyNative(nint context, ulong generation)
        {
            var entry = (Entry?)GCHandle.FromIntPtr(context).Target;
            if (entry is null) return;
            var proxy = entry.Owner;
            Interlocked.Increment(ref proxy._readies);
            try
            {
                Check(generation == 0, "Every real one-shot command ready uses generation zero.");
                try { proxy.BeforeManaged?.Invoke(entry.Kind); } catch (Exception error) { proxy.Record(error); }
                _inManagedReady = true;
                try { entry.Callbacks.Ready(entry.Callbacks.Context, generation); }
                finally { _inManagedReady = false; }
                try { proxy.AfterManaged?.Invoke(entry.Kind); } catch (Exception error) { proxy.Record(error); }
            }
            catch (Exception error) { proxy.Record(error); }
            finally { proxy.Release(context); }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static uint TakeNative(nint command, uint kind, NativeCommandResult* result, NwErrorInfoV1* error)
        {
            var proxy = Volatile.Read(ref _active);
            if (proxy is null) return NativeStatus.ContractViolation;
            Interlocked.Increment(ref proxy._takes);
            var status = proxy._original.Take(command, kind, result, error);
            if (status == NativeStatus.Ok && result != null && result->Owner != 0)
                Volatile.Write(ref proxy._transferredOwner, result->Owner);
            return status;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static uint ErrorNative(nint command, nint* buffer, NwErrorInfoV1* error)
        {
            var proxy = Volatile.Read(ref _active);
            if (proxy is null) return NativeStatus.ContractViolation;
            Interlocked.Increment(ref proxy._errors);
            return proxy._original.TakeError(command, buffer, error);
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static uint CancelNative(nint command)
        {
            var proxy = Volatile.Read(ref _active);
            if (proxy is null) return NativeStatus.ContractViolation;
            Interlocked.Increment(ref proxy._cancels);
            return proxy._original.Cancel(command);
        }

        public void Dispose()
        {
            _source.Binding.Completions = _original;
            lock (_gate) Check(_callbacks.Count == 0, "The command forwarding fixture outlives every actual native notification.");
            Interlocked.CompareExchange(ref _active, null, this);
        }
    }
}
