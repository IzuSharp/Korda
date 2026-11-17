using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.Sqlite;
using NativeWeave.Database.PostgreSql;
using NativeWeave.Interop;
using NativeWeave.Interop.Async;
using System.Diagnostics;
using System.Buffers.Binary;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Callbacks;
using NativeWeave.Interop.Lifetime;
using NativeWeave.Query.Internal;

if (args.Contains("--manifest-bounds")) { Suite.ManifestBounds(); return; }
if (args.Length > 0) { await Suite.Worker(args); return; }
await Suite.Run();

internal static class Suite
{
    private static int _assertions, _retirements;
    internal static void ManifestBounds()
    {
        _ = new MigrationBundle(DatabaseProvider.Sqlite, [new(1, "warm", "SELECT 1")]);
        string script = new('é', 800000);
        Migration[] entries = [new(1, "one", script), new(2, "two", script), new(3, "three", script)];
        long before = GC.GetAllocatedBytesForCurrentThread(); bool rejected = false;
        try { _ = new MigrationBundle(DatabaseProvider.Sqlite, entries); } catch (ArgumentException) { rejected = true; }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(rejected && allocated < 65536, "aggregate UTF-8 size rejects before script-sized allocation");
        // Exact kind12 overhead:104 + one entry144 + description1.
        var exact = new MigrationBundle(DatabaseProvider.Sqlite, [new(1, "x", new string('s', 4 * 1024 * 1024 - 249))]);
        Check(exact.Bytes.Length == 4 * 1024 * 1024, "exact finite wire boundary is admitted");
        Console.WriteLine($"B08 focused manifest bounds: {_assertions} assertions; rejection allocated={allocated} bytes");
    }
    private static void Check(bool value, string message) { Interlocked.Increment(ref _assertions); if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { Interlocked.Increment(ref _assertions); return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static DataSource Source(DatabaseProvider provider, string connection, DatabaseLimits? limits = null)
    {
        var builder = provider == DatabaseProvider.Sqlite ? new DataSourceBuilder().UseSqlite(connection) : new DataSourceBuilder().UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        return builder.ConfigureLimits(limits ?? new DatabaseLimits { MaxConnections = 1, MaxWaiters = 2 }).Build();
    }
    private static void Observe(DataSource source)
    {
        source.TestRetirement = snapshot =>
        {
            Check(snapshot.Released && !snapshot.RootLive && snapshot.Controls == 0 && snapshot.CallbackEntries == 0 && snapshot.RetainedInputBytes == 0, "independent managed retirement is safe");
            Check(snapshot.Start is AsyncStartOutcome.Skipped or AsyncStartOutcome.Rejected or AsyncStartOutcome.NotStarted || snapshot.NativeQuiet, "native operation retires before managed completion");
            Interlocked.Increment(ref _retirements);
        };
    }
    internal static async Task Run()
    {
        var pg = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES") ?? throw new InvalidOperationException("A real PostgreSQL server is required.");
        var directory = Path.Combine(Path.GetTempPath(), "nw-b08-managed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var sqlite = Path.Combine(directory, "main.db");
        foreach (var provider in new[] { DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql })
        {
            Console.WriteLine("migrations provider=" + provider);
            var connection = provider == DatabaseProvider.Sqlite ? sqlite : pg;
            var source = Source(provider, connection); Observe(source);
            Check(source.Capabilities.Supports("db.migrations.v1") && source.Capabilities.Supports("db.schema.read.v1"), "real capability mapping");
            Check(ReferenceEquals(source.Administration, source.Administration), "one lazy administration facade");
            if (provider == DatabaseProvider.Sqlite) Check(!File.Exists(sqlite), "administration property does not open SQLite");
            var migrations = new List<Migration> { new(1, "create", "CREATE TABLE b08_items(id INTEGER PRIMARY KEY, name TEXT NOT NULL)"), new(2, "seed", "INSERT INTO b08_items VALUES(1,'one')") };
            var bundle = new MigrationBundle(provider, migrations); var hash = bundle.ContentHash; migrations.Clear();
            Check(hash.Length == 64 && hash.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c)), "separate canonical bundle SHA256");
            var pending = await source.Administration.GetMigrationStatusAsync(bundle); Check(pending.Count == 2 && pending.All(x => !x.Applied), "fresh pending ledger snapshot");
            Check((await source.Administration.GetTablesAsync()).Count == 0, "status does not create ledger/schema");
            await source.Administration.ValidateMigrationsAsync(bundle);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var cancellation = await Throws<OperationCanceledException>(() => source.Administration.MigrateAsync(bundle, cancellationToken: canceled.Token)); Check(cancellation.CancellationToken == canceled.Token, "pre-admission token identity");
            await using var requestSession = await source.OpenSessionAsync();
            await using (var tx = await requestSession.BeginTransactionAsync())
            {
                var first = await source.Administration.MigrateAsync(bundle, new() { TargetVersion = 1 }); Check(first == new MigrationReport(1, 1, true), "administration uses a separate connection from request transaction");
                await tx.RollbackAsync();
            }
            var second = await source.Administration.MigrateAsync(bundle); Check(second == new MigrationReport(1, 2, true), "forward target then remaining range");
            Check(await requestSession.ScalarAsync(SqlCommand.Create("SELECT CAST(count(*) AS BIGINT) FROM b08_items"), Codecs.Int64) == 1, "actual script result");
            Check((await source.Administration.MigrateAsync(bundle)).AppliedCount == 0, "same hash replay is idempotent");
            var statuses = await source.Administration.GetMigrationStatusAsync(bundle); Check(statuses.All(x => x.Applied && x.Checksum.Length == 96), "SQLx SHA384 is not replaced by bundle SHA256");
            await source.Administration.ValidateMigrationsAsync(bundle);
            var tables = await source.Administration.GetTablesAsync(); var table = tables.Single(x => x.Name == "b08_items");
            Check(table.Columns.Count == 2 && table.Columns[0].IsPrimaryKey && !table.Columns[0].IsNullable && !table.Columns[1].IsNullable, "catalog ordered columns/nullability/key");
            Check(table.Columns is System.Collections.ObjectModel.ReadOnlyCollection<SchemaColumn> && tables is System.Collections.ObjectModel.ReadOnlyCollection<SchemaTable>, "returned collections are immutable");
            await Throws<ArgumentException>(() => source.Administration.MigrateAsync(bundle, new() { TargetVersion = 1 }));
            var changed = new MigrationBundle(provider, [new(1,"create","CREATE TABLE changed(id INTEGER)"),new(2,"seed","INSERT INTO b08_items VALUES(1,'one')")]);
            var mismatch = await Throws<DatabaseException>(() => source.Administration.MigrateAsync(changed)); Check(mismatch.Kind == DatabaseErrorKind.InvalidState && mismatch.CorrelationId != 0, "native migration checksum classification");
            await Throws<ArgumentException>(() => source.Administration.GetTablesAsync("public; DROP TABLE b08_items"));
            await Throws<ArgumentException>(() => source.Administration.MigrateAsync(new(provider, [new(1,"escape","COMMIT")] )));
            var wrong = new MigrationBundle(provider == DatabaseProvider.Sqlite ? DatabaseProvider.PostgreSql : DatabaseProvider.Sqlite, []);
            await Throws<ArgumentException>(() => source.Administration.MigrateAsync(wrong));
            // Managed rich-error mapping failure must release the separately owned native buffer.
            source.TestAfterErrorTake = () => throw new MappingFailure();
            await Throws<MappingFailure>(() => source.Administration.MigrateAsync(changed)); source.TestAfterErrorTake = null;
            await source.Administration.ValidateMigrationsAsync(bundle);
            await requestSession.DisposeAsync(); await source.DisposeAsync(); Check(source.Cleanup.LocalResourcesReleased, "source closed after administration");
            await Throws<ObjectDisposedException>(() => source.Administration.GetTablesAsync());
            await using (var bounded = Source(provider, connection, new DatabaseLimits { MaxBufferedRows = 1 }))
            { Observe(bounded); var failure = await Throws<DatabaseException>(() => bounded.Administration.GetTablesAsync()); Check(failure.Kind == DatabaseErrorKind.ResourceLimit, "nested table/column records share the row budget"); }
            if (provider == DatabaseProvider.Sqlite) await SqliteFailureAndCancellation(directory);
            else await PostgreSqlFailureAndCancellation(pg, bundle);
        }
        await WaitZero(SqliteDataSource.Engine); await WaitZero(PostgreSqlDataSource.Engine); Directory.Delete(directory, true);
        Console.WriteLine($"B08 actual Linux two-provider assertions={_assertions}; managed retirements={_retirements}; all native counters=0");
    }
    private static async Task SqliteFailureAndCancellation(string directory)
    {
        var boundedPath=Path.Combine(directory,"bounded.db");
        await using (var bounded=Source(DatabaseProvider.Sqlite,boundedPath,new DatabaseLimits {MaxBufferedRows=1}))
        {
            var tooMany=new MigrationBundle(DatabaseProvider.Sqlite,[new(1,"first","CREATE TABLE too_many(id INTEGER)"),new(2,"second","INSERT INTO too_many VALUES(1)")]);
            var capacity=await Throws<DatabaseException>(()=>bounded.Administration.MigrateAsync(tooMany));
            Check(capacity.Kind==DatabaseErrorKind.ResourceLimit && !File.Exists(boundedPath),"history budget rejects before connection or DDL");
        }
        var path = Path.Combine(directory,"rollback.db"); await using var source = Source(DatabaseProvider.Sqlite,path); Observe(source);
        var failed = new MigrationBundle(DatabaseProvider.Sqlite,[new(1,"one","CREATE TABLE first_entry(id INTEGER)"),new(2,"failure","INSERT INTO missing_table VALUES(1)")]);
        await Throws<DatabaseException>(() => source.Administration.MigrateAsync(failed)); Check((await source.Administration.GetTablesAsync()).Count == 0,"SQLite failed second migration rolls back complete batch and ledgers");
        var nontransactional = new MigrationBundle(DatabaseProvider.Sqlite,[new(1,"no_tx","CREATE TABLE bad(id INTEGER)",MigrationTransactionMode.NonTransactional)]);
        var unsupported=await Throws<DatabaseCapabilityException>(() => source.Administration.MigrateAsync(nontransactional,new() {AllowNonTransactional=true})); Check(unsupported.Capability=="db.migrations.v1" && unsupported.Provider=="Sqlite","unsupported profile preserves stable capability/provider identities");
        var longScript = new MigrationBundle(DatabaseProvider.Sqlite,[new(1,"cancel","CREATE TABLE canceled AS WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<100000000) SELECT v FROM n")]);
        using var cancel = new CancellationTokenSource(); var pending = source.Administration.MigrateAsync(longScript,cancellationToken:cancel.Token); await Task.Delay(30); cancel.Cancel();
        var stopped = await Throws<OperationCanceledException>(() => pending); Check(stopped.CancellationToken==cancel.Token && pending.IsCanceled,"SQLite progress cancellation keeps token and canceled Task status"); Check(stopped.InnerException is DatabaseException,"actual SQLite interruption preserves failed rollback as cleanup InnerException"); Check((await source.Administration.GetTablesAsync()).Count==0,"SQLite cancellation rollback completes before return");
        await ObserveCleanupSnapshot(source,longScript,true);
        // Source shutdown requests cancellation and awaits actual administration retirement.
        var closing = source.Administration.MigrateAsync(longScript); await Task.Delay(25); await source.DisposeAsync();
        await Throws<OperationCanceledException>(()=>closing);Check(source.Cleanup.LocalResourcesReleased,"source close retains admin worker until rollback and close");
    }
    private static async Task PostgreSqlFailureAndCancellation(string connection, MigrationBundle original)
    {
        await using var source=Source(DatabaseProvider.PostgreSql,connection);Observe(source);
        var next = new MigrationBundle(DatabaseProvider.PostgreSql,[..original.Entries,new(3,"cancel","CREATE TABLE canceled(id INTEGER); SELECT pg_sleep(0.2)")]);
        using var cancellation = new CancellationTokenSource(); var watch=Stopwatch.StartNew();var pending=source.Administration.MigrateAsync(next,cancellationToken:cancellation.Token);
        await Task.Delay(30);cancellation.Cancel();await Throws<OperationCanceledException>(()=>pending);Check(pending.IsCanceled,"ordinary PG cancellation retains canceled Task status");Check(watch.Elapsed>=TimeSpan.FromMilliseconds(180),"PG cancellation retains the dispatched native future");
        Check(!(await source.Administration.GetTablesAsync()).Any(x=>x.Name=="canceled"),"PG canceled migration explicitly rolled back");
        var successful = new MigrationBundle(DatabaseProvider.PostgreSql,[..original.Entries,new(3,"commit","CREATE TABLE committed(id INTEGER)")]);
        Check((await source.Administration.MigrateAsync(successful)).AppliedCount==1,"next migration succeeds after canceled dedicated connection closed");
        var future = new MigrationBundle(DatabaseProvider.PostgreSql,[..successful.Entries,new(4,"future index","CREATE INDEX CONCURRENTLY future_index ON committed(id)",MigrationTransactionMode.NonTransactional)]);
        Check((await source.Administration.MigrateAsync(future,new() {TargetVersion=3})).AppliedCount==0,"unselected future nontransactional entry needs no execution opt-in");
        var terminated=new MigrationBundle(DatabaseProvider.PostgreSql,[..successful.Entries,new(4,"terminate","SELECT pg_terminate_backend(pg_backend_pid())")]);
        var failure=await Throws<DatabaseException>(()=>source.Administration.MigrateAsync(terminated));
        Check(failure.ProviderCode=="57P01" && failure.Kind==DatabaseErrorKind.Provider && failure.InnerException is DatabaseException,"actual PG provider failure retains failed rollback/close without replacing primary identity");
        await ObserveCleanupSnapshot(source,terminated,false);
    }
    private static async Task ObserveCleanupSnapshot(DataSource source,MigrationBundle bundle,bool cancel)
    {
        using var token=new CancellationTokenSource();
        using var coordinator=new CallbackCoordinator(4);
        using var resource=new NativeResourceOwner(new OperationHandle(source.Binding,CreateRawAdmin(source,bundle)),source);
        var calls=new ErrorSnapshotCalls(source);
        var operation=new AsyncOperation<bool>(resource,calls,coordinator,_=>throw new InvalidOperationException("Failure operation cannot transfer a success result."),token.Token);
        var task=operation.Start();
        if(cancel){await Task.Delay(30);token.Cancel();await Throws<OperationCanceledException>(()=>task);}
        else await Throws<DatabaseException>(()=>task);
        await operation.Retirement;
        Check(calls.Observed,"actual owned kind7 cleanup record was observed");
        Check(operation.Snapshot.Released && operation.Snapshot.NativeQuiet && !operation.Snapshot.RootLive,"raw admin ownership retired independently");
        Interlocked.Increment(ref _retirements);
    }
    private static unsafe nint CreateRawAdmin(DataSource source,MigrationBundle bundle)
    {
        var payload=AdministrationEncoding.Request(source,0,bundle,new MigrationOptions(),null);
        using var lease=source.Owner.Borrow(DatabaseOwnerKind.Source);nint operation=0;var error=BindingChecks.Error();
        fixed(byte* bytes=payload)
            HostFailure.Check(source.Binding,source.Binding.Calls.nw_db_admin_create_v1(lease.Pointer,0,BindingChecks.View(bytes,payload.Length),&operation,&error),error);
        return operation;
    }
    private sealed unsafe class ErrorSnapshotCalls(DataSource source):AsyncNativeCalls
    {
        internal bool Observed;
        internal override uint Start(nint operation,in NwOperationCallbacksV1 callbacks)
        {var error=BindingChecks.Error();fixed(NwOperationCallbacksV1* pointer=&callbacks)return source.Binding.Calls.nw_db_operation_start_v1(operation,pointer,&error);}
        internal override uint RequestCancel(nint operation)=>source.Binding.Calls.nw_db_operation_request_cancel_v1(operation);
        internal override uint TryRetire(nint operation)=>source.Binding.Calls.nw_db_operation_try_retire_v1(operation);
        internal override void Poison()=>source.Poison();
        internal override Exception MapFailure(nint operation,uint status,in NwErrorInfoV1 error,CancellationToken token)
        {
            nint owner=0;var detail=BindingChecks.Error();HostFailure.Check(source.Binding,source.Binding.Calls.nw_db_operation_take_error_v1(operation,&owner,&detail),detail);
            using var buffer=new NativeWeave.Database.NativeBuffer(source.Binding,owner);var fields=HostEncoding.ReadControl(buffer.Copy(),7);
            Check(BinaryPrimitives.ReadUInt32LittleEndian(fields[4])==(uint)CleanupStatus.Failed,"actual cleanup failure flag");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(fields[5])==3 && (uint)ConnectionDisposition.Discarded==3,"canonical discarded disposition is 3");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(fields[6])==1,"dedicated connection local owner released despite cleanup failure");
            Observed=true;var provider=fields.TryGetValue(9,out var code)?HostEncoding.Utf8.GetString(code):null;
            return HostFailure.Create(source.Binding,status,error,provider,token);
        }
    }
    internal static async Task Worker(string[] args)
    {
        var provider=args[1]=="sqlite"?DatabaseProvider.Sqlite:DatabaseProvider.PostgreSql;
        await using var source=Source(provider,args[2],new DatabaseLimits { CommandTimeout=TimeSpan.FromSeconds(30) });
        var sql=args[0] switch
        {
            "contend"=>"CREATE TABLE worker_state(id INTEGER PRIMARY KEY); INSERT INTO worker_state VALUES(1)",
            "crash" when provider==DatabaseProvider.Sqlite=>"CREATE TABLE worker_state AS WITH RECURSIVE n(v) AS (VALUES(1) UNION ALL SELECT v+1 FROM n WHERE v<10000000) SELECT v FROM n",
            "crash"=>"CREATE TABLE worker_state(id INTEGER PRIMARY KEY); SELECT pg_sleep(1); INSERT INTO worker_state VALUES(1)",
            _=>throw new ArgumentException("worker mode")
        };
        var result=await source.Administration.MigrateAsync(new(provider,[new(1,"worker",sql)]));
        Console.WriteLine($"worker completed applied={result.AppliedCount} current={result.CurrentVersion}");
    }
    private static async Task WaitZero(EngineDescriptor engine)
    {
        for(var i=0;i<100;i++){if(Counts(engine).All(x=>x==0)){Check(true,"all actual native owners retired");return;}await Task.Delay(10);}
        throw new InvalidOperationException("Native owner leak: "+string.Join(',',Counts(engine)));
    }
    private static unsafe ulong[] Counts(EngineDescriptor engine)
    {var count=(delegate* unmanaged[Cdecl]<uint,ulong>)EngineModuleLoader.ResolveExport(engine,"nw_db_diagnostic_count_v1");return Enumerable.Range(0,6).Select(i=>count((uint)i)).ToArray();}
    private sealed class MappingFailure:Exception;
}
