using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using Korda.Data;
using Korda.Database;
using Korda.Database.Sqlite;
using Korda.Database.PostgreSql;
using Korda.Interop;
using Korda.Interop.Async;
using Korda.Interop.Abi;
using Korda.Query.Internal;
using System.Buffers.Binary;

if (await SnapshotProbe.RunIfRequested(args)) return;
if (await FetchProbe.RunIfRequested(args)) return;
if (await TransactionDefaultProbe.RunIfRequested(args)) return;
Console.WriteLine("snapshot parser oracle inputs=" + SnapshotProbe.Verify());
SnapshotProbe.VerifyAllocationBudget();
try { await Suite.Run(); } catch (DatabaseException failure) { Console.Error.WriteLine($"kind={failure.Kind} provider={failure.ProviderCode} correlation={failure.CorrelationId}"); throw; }

internal static class Suite
{
    private static int _assertions, _retirements;
    private static void Check(bool value, string message) { Interlocked.Increment(ref _assertions); if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T exception) { Interlocked.Increment(ref _assertions); return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    internal static async Task Run()
    {
        var pg = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES");
        if (string.IsNullOrEmpty(pg)) throw new InvalidOperationException("Set NW_TEST_POSTGRES to the isolated actual PostgreSQL18 test cluster.");
        var directory = Path.Combine(Path.GetTempPath(), "korda-b04-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "host.db");
        var sqlite = new DataSourceBuilder().UseSqlite(path).WithName("b04-sqlite").ConfigureLimits(new DatabaseLimits { MaxConnections = 2, MaxBatchRows = 3 }).Build();
        Check(!File.Exists(path), "Build must not open SQLite");
        var postgres = new DataSourceBuilder().UsePostgreSql(pg, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment).WithName("b04-postgres").ConfigureLimits(new DatabaseLimits { MaxConnections = 2, MaxBatchRows = 3 }).Build();
        WrongModule(sqlite, postgres); WrongModule(postgres, sqlite);
        foreach (var source in new[] { sqlite, postgres })
        {
            Console.WriteLine("host provider=" + source.Provider);
            source.TestRetirement = snapshot =>
            {
                Check(snapshot.Released && !snapshot.RootLive && snapshot.Controls == 0 && snapshot.CallbackEntries == 0 && snapshot.RetainedInputBytes == 0, "managed retirement released root/control/callback/input owners");
                Check(snapshot.Start is AsyncStartOutcome.Skipped or AsyncStartOutcome.Rejected or AsyncStartOutcome.NotStarted || snapshot.NativeQuiet, "accepted native operation is quiet before managed result returns");
                Interlocked.Increment(ref _retirements);
            };
            Check(source.Capabilities.Supports("db.sql.expert.v1"), "actual raw SQL capability");
            Check(!source.Capabilities.Supports("unknown"), "unknown capability false");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var canceledOpen = source.OpenSessionAsync(canceled.Token);
            Check(canceledOpen.IsCanceled, "already canceled Open Task");
            var ce = await Throws<OperationCanceledException>(() => canceledOpen); Check(ce.CancellationToken == canceled.Token, "token identity");
            await using (var probe = await source.Start(() => NativeHost.Open(source), operation => TakeProbe(source, operation))) { Check(probe.Cleanup.Status == CleanupStatus.NotStarted, "adopted session independent from operation retirement"); }
            var session = await source.OpenSessionAsync();
            await using (var occupied = await source.OpenSessionAsync())
            {
                using var pendingCancel = new CancellationTokenSource();
                var pending = source.OpenSessionAsync(pendingCancel.Token);
                await Task.Delay(20); Check(!pending.IsCompleted, "physical capacity bounds accepted admission");
                pendingCancel.Cancel(); var canceledAdmission = await Throws<OperationCanceledException>(() => pending);
                Check(canceledAdmission.CancellationToken == pendingCancel.Token, "accepted cancel token identity");
            }
            var table = "b04_" + Guid.NewGuid().ToString("N");
            await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE " + table + " (id BIGINT PRIMARY KEY, name TEXT NOT NULL)", kind: SqlCommandKind.Mutation));
            for (var i = 1L; i <= 7; i++)
            {
                var command = SqlCommand.Create("INSERT INTO " + table + " (id,name) VALUES (@id,@name)", new SqlParameters().Add("id", i, Codecs.Int64).Add("name", "row " + i, Codecs.String), SqlCommandKind.Mutation);
                Check(await session.ExecuteAsync(command) == 1, "affected count");
            }
            Check(await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM " + table), Codecs.Int64) == 7, "exact scalar count");
            var nullable = Codecs.Nullable(Codecs.Int64);
            Check(await session.ScalarAsync(SqlCommand.Create("SELECT CAST(@empty AS BIGINT)", new SqlParameters().Add("empty", (long?)null, nullable)), nullable) is null, "typed null remains a null canonical wire cell");
            var repeated = SqlCommand.Parameterized(FormattableStringFactory.Create("SELECT CAST({0} AS BIGINT) + CAST({0} AS BIGINT)", 3L));
            Check(await session.ScalarAsync(repeated, Codecs.Int64) == 6, "repeated interpolated argument is bound twice without SQL interpolation");
            var quotedHole = SqlCommand.Parameterized(FormattableStringFactory.Create("SELECT {0}, '{0}'", 3L));
            await Throws<ArgumentException>(() => session.ScalarAsync(quotedHole, Codecs.Int64));
            var projection = Projection<Row>.Create([ResultField.Create("id", Codecs.Int64), ResultField.Create("name", Codecs.String)], row => new(row.Read(0, Codecs.Int64), row.Read(1, Codecs.String)));
            await NativeBatches(source, session, table, projection);
            var rows = await session.QueryAsync(SqlCommand.Create("SELECT id,name FROM " + table + " ORDER BY id"), projection);
            Check(rows.Count == 7 && rows[6].Id == 7 && rows[0].Name == "row 1", "multi-batch owned materialization");
            var empty = await session.QueryAsync(SqlCommand.Create("SELECT id,name FROM " + table + " WHERE id<0"), projection); Check(empty.Count == 0, "empty canonical EOS");
            await Throws<InvalidOperationException>(() => session.ScalarAsync(SqlCommand.Create("SELECT id FROM " + table), Codecs.Int64));
            await Throws<InvalidOperationException>(() => session.ScalarAsync(SqlCommand.Create("SELECT id FROM " + table + " WHERE id<0"), Codecs.Int64));
            var shapeFailure = await Throws<DatabaseException>(() => session.QueryAsync(SqlCommand.Create("SELECT id AS wrong,name FROM " + table), projection)); Check(shapeFailure.Kind == DatabaseErrorKind.ShapeMismatch, "empty/nonempty exact shape");
            var mapper = Projection<Row>.Create([ResultField.Create("id", Codecs.Int64), ResultField.Create("name", Codecs.String)], _ => throw new MappingFailure());
            await Throws<MappingFailure>(() => session.QueryAsync(SqlCommand.Create("SELECT id,name FROM " + table), mapper));
            Check(await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM " + table), Codecs.Int64) == 7, "mapping exception drains session");
            await using (var stream = session.StreamAsync(SqlCommand.Create("SELECT id,name FROM " + table + " ORDER BY id"), projection).GetAsyncEnumerator())
            {
                Check(await stream.MoveNextAsync() && stream.Current.Id == 1, "lazy bounded stream");
                var busy = await Throws<DatabaseException>(() => session.ExecuteAsync(SqlCommand.Create("DELETE FROM " + table + " WHERE id=-1", kind: SqlCommandKind.Mutation))); Check(busy.Kind == DatabaseErrorKind.ConcurrentUse, "cursor holds session admission");
            }
            await using (var transaction = await session.BeginTransactionAsync())
            {
                await session.ExecuteAsync(SqlCommand.Create("INSERT INTO " + table + " VALUES (8,'outer')", kind: SqlCommandKind.Mutation));
                await using (var savepoint = await transaction.CreateSavepointAsync())
                { await session.ExecuteAsync(SqlCommand.Create("INSERT INTO " + table + " VALUES (9,'savepoint')", kind: SqlCommandKind.Mutation)); await savepoint.RollbackAsync(); await savepoint.ReleaseAsync(); }
                await transaction.CommitAsync(); Check(transaction.Outcome == TransactionOutcome.Committed, "confirmed commit");
            }
            Check(await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM " + table), Codecs.Int64) == 8, "savepoint rollback effect");
            await using (var transaction = await session.BeginTransactionAsync())
            { await session.ExecuteAsync(SqlCommand.Create("DELETE FROM " + table, kind: SqlCommandKind.Mutation)); await transaction.RollbackAsync(); Check(transaction.Outcome == TransactionOutcome.RolledBack, "confirmed rollback"); }
            var duplicate = await Throws<DatabaseException>(() => session.ExecuteAsync(SqlCommand.Create("INSERT INTO " + table + " VALUES (1,'duplicate')", kind: SqlCommandKind.Mutation)));
            Check(duplicate.Kind == DatabaseErrorKind.Constraint && duplicate.ProviderCode is not null, "bounded provider error identity");
            source.TestAfterErrorTake = () => throw new MappingFailure();
            await Throws<MappingFailure>(() => session.ExecuteAsync(SqlCommand.Create("INSERT INTO " + table + " VALUES (1,'duplicate')", kind: SqlCommandKind.Mutation))); source.TestAfterErrorTake = null;
            foreach (var fault in new[] { AsyncBoundary.BeforeRoot, AsyncBoundary.RootAllocated, AsyncBoundary.BeforeRegister, AsyncBoundary.Registered, AsyncBoundary.BeforeStart, AsyncBoundary.BeforeTake })
            {
                source.TestBoundary = boundary => { if (boundary == fault) throw new MappingFailure(); };
                await Throws<MappingFailure>(() => source.OpenSessionAsync()); source.TestBoundary = null;
            }
            source.TestAfterOwnerTake = () => throw new MappingFailure(); await Throws<MappingFailure>(() => source.OpenSessionAsync()); source.TestAfterOwnerTake = null;
            Check(await source.CanConnectAsync(), "fault cleanup restores physical capacity");
            await Throws<TimeoutException>(() => session.ExecuteAsync(SqlCommand.Create("DELETE FROM " + table + " WHERE id=-1", kind: SqlCommandKind.Mutation, timeout: TimeSpan.Zero)));
            await session.ExecuteAsync(SqlCommand.Create("DROP TABLE " + table, kind: SqlCommandKind.Mutation));
            await session.DisposeAsync(); Check(session.Cleanup.LocalResourcesReleased, "session physical retirement");
            Check(session.Cleanup.ConnectionDisposition == ConnectionDisposition.Usable, "normal session receipt confirms pool return");
            await using (var changing = await source.OpenSessionAsync())
            {
                var sql = source.Provider == DatabaseProvider.Sqlite ? "PRAGMA user_version = 17" : "SET application_name = 'b04-session-mutating'";
                await changing.ExecuteAsync(SqlCommand.Create(sql, kind: SqlCommandKind.SessionMutating));
                Check(changing.Cleanup.LocalResourcesReleased && changing.Cleanup.ConnectionDisposition == ConnectionDisposition.Discarded, "session-mutating command physically discards connection");
                await Throws<InvalidOperationException>(() => changing.ExecuteAsync(SqlCommand.Create("DELETE FROM imaginary", kind: SqlCommandKind.Mutation)));
            }
            var orphan = await source.OpenSessionAsync();
            var close = source.CloseAsync(TimeSpan.Zero); var snapshot = await close; Check(snapshot.Status is CleanupStatus.Pending or CleanupStatus.Succeeded, "zero close observation");
            await Throws<OperationCanceledException>(() => source.CloseAsync(TimeSpan.FromSeconds(1), canceled.Token));
            await source.DisposeAsync(); Check(source.Cleanup.LocalResourcesReleased, "engine runtime retirement");
            await Throws<ObjectDisposedException>(() => orphan.ScalarAsync(SqlCommand.Create("SELECT 1"), Codecs.Int64));
            await orphan.DisposeAsync(); Check(orphan.Cleanup.LocalResourcesReleased, "child after source retirement");
            await source.DisposeAsync();
            await WaitZero(source.Provider == DatabaseProvider.Sqlite ? SqliteDataSource.Engine : PostgreSqlDataSource.Engine);
        }
        await DependencyInjection(Path.Combine(directory, "di.db"));
        await WaitZero(SqliteDataSource.Engine);
        Directory.Delete(directory, true);
        Console.WriteLine("B04 actual Linux two-provider assertions=" + _assertions + "; separately observed managed retirements=" + _retirements + "; all source/session/operation/cursor/buffer/handle counters=0");
    }
    private static unsafe void WrongModule(DataSource owner, DataSource foreign)
    {
        using var lease = owner.Owner.Borrow(DatabaseOwnerKind.Source); nint output = 123; var error = BindingChecks.Error();
        var status = foreign.Binding.Calls.nw_db_session_open_create_v1(lease.Pointer, &output, &error);
        Check(status == NativeStatus.InvalidState && output == 0, "foreign creating-module owner rejects and zeros output");
    }
    private static unsafe DataSession TakeProbe(DataSource source, nint operation)
    {
        nint pointer = 123; var error = BindingChecks.Error();
        var wrong = source.Binding.Calls.nw_db_operation_take_owner_v1(operation, 2, &pointer, &error);
        Check(wrong == NativeStatus.InvalidState && pointer == 0, "wrong typed take is nonconsuming and clears output");
        var owner = NativeHost.TakeOwner(source.Binding, operation, 1, DatabaseOwnerKind.Session);
        pointer = 123; var twice = source.Binding.Calls.nw_db_operation_take_owner_v1(operation, 1, &pointer, &error);
        Check(twice == NativeStatus.InvalidState && pointer == 0, "owner transfers exactly once");
        return new(source, owner);
    }
    private static async Task NativeBatches(DataSource source, DataSession session, string table, Projection<Row> projection)
    {
        var id = source.RegisterProjection(projection);
        using var plan = SqlSubmission.Create(session, SqlCommand.Create("SELECT id,name FROM " + table + " ORDER BY id"), 3, id);
        using var cursor = await session.ExecutePlanAsync(plan, ReadOnlyMemory<byte>.Empty, default);
        var close = new CloseController(source, cursor, cursor.Dispose);
        Task<Korda.Database.NativeBuffer> Fetch(int rows) => source.Start(() => NativeHost.Fetch(cursor, rows, source.Limits.MaxBatchBytes), operation => NativeHost.TakeBuffer(source.Binding, operation));
        try
        {
            using (var first = await Fetch(1))
            {
                using var batch = new HostStreamBatch(first, checked((int)source.Limits.MaxBatchBytes));
                var borrowed = batch.Bytes;
                var transferred = batch.TakeBytes();
                Check(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(borrowed, out var segment)
                    && ReferenceEquals(segment.Array, transferred), "host transfers its existing managed copy without cloning");
                Check(BinaryPrimitives.ReadUInt32LittleEndian(transferred.AsSpan(56)) == 1, "native per-fetch one-row demand");
                await Throws<ObjectDisposedException>(() => { _ = batch.Bytes; return Task.CompletedTask; });
                await Throws<ObjectDisposedException>(() => { _ = batch.TakeBytes(); return Task.CompletedTask; });
                await Throws<InvalidOperationException>(async () => { using var blocked = await Fetch(1); });
                Check(!first.IsClosed, "managed transfer retains the native byte permit until batch disposal");
                batch.Dispose(); batch.Dispose();
                Check(first.IsClosed && BinaryPrimitives.ReadUInt32LittleEndian(transferred.AsSpan(56)) == 1,
                    "host batch disposal releases native owner once while transferred managed bytes survive");
            }
            using var second = await Fetch(2);
            Check(BinaryPrimitives.ReadUInt32LittleEndian(second.Copy().AsSpan(56)) == 2, "native per-fetch two-row demand after prior buffer release");
        }
        finally { close.Request(); await close.JoinAsync(); }
    }
    private static async Task DependencyInjection(string path)
    {
        var services = new ServiceCollection(); var configurations = 0;
        services.AddKordaDatabase(builder => { configurations++; builder.UseSqlite(path); });
        services.AddKeyedKordaDatabase("audit", builder => builder.UseSqlite(path));
        Check(configurations == 0, "DI configuration waits for owner construction");
        await Throws<InvalidOperationException>(() => { services.AddKordaDatabase(_ => { }); return Task.CompletedTask; });
        await Throws<InvalidOperationException>(() => { services.AddKeyedKordaDatabase("audit", _ => { }); return Task.CompletedTask; });
        DataSource first, keyed;
        await using (var provider = services.BuildServiceProvider())
        {
            first = provider.GetRequiredService<DataSource>(); keyed = provider.GetRequiredKeyedService<DataSource>("audit");
            Check(ReferenceEquals(first, provider.GetRequiredService<DataSource>()) && configurations == 1, "DI singleton config exactly once");
            Check(!ReferenceEquals(first, keyed) && keyed.Name == "audit", "keyed sources have independent ownership");
            Check(provider.GetService<DataSession>() is null, "no implicitly shared session");
            Check(await first.CanConnectAsync() && await keyed.CanConnectAsync(), "DI sources open actual SQLite");
        }
        Check(first.Cleanup.LocalResourcesReleased && keyed.Cleanup.LocalResourcesReleased, "async container disposal joins native sources");
    }
    private static async Task WaitZero(EngineDescriptor engine)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var counts = Counts(engine); if (counts.All(value => value == 0)) { Check(true, "all native resources retired"); return; }
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Native resource leak: " + string.Join(',', Counts(engine)));
    }
    private static unsafe ulong[] Counts(EngineDescriptor engine)
    { var count = (delegate* unmanaged[Cdecl]<uint, ulong>)EngineModuleLoader.ResolveExport(engine, "nw_db_diagnostic_count_v1"); return Enumerable.Range(0, 6).Select(index => count((uint)index)).ToArray(); }
    private sealed record Row(long Id, string Name);
    private sealed class MappingFailure : Exception;
}
