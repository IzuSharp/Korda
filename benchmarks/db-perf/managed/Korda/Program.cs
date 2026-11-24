using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using Korda.Data;
using Korda.Database;
#if POSTGRES
using Korda.Database.PostgreSql;
#else
using Korda.Database.Sqlite;
#endif

if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("DB-Perf-01 must execute a published Native AOT binary.");
var options = Options.Parse(args);
var builder = new DataSourceBuilder().WithName("DB-Perf-01")
    .ConfigureLimits(new DatabaseLimits
    {
        MaxConnections = options.Concurrency + 1,
        MaxWaiters = Math.Max(16, options.Concurrency * 2),
        MaxBatchRows = options.BatchRows,
        MaxFieldBytes = 4096,
        MaxBufferedRows = Math.Max(10000, options.Rows),
        MaxBufferedBytes = 256L * 1024 * 1024,
        MaxPreparedPlans = Math.Max(128, options.Concurrency * 40),
        MaxPreparedPlanBytes = Math.Max(8L, options.Concurrency) * 1024 * 1024,
    });
#if POSTGRES
builder.UsePostgreSql(options.Connection, profile => profile.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
#else
builder.UseSqlite(options.Connection, profile =>
{
    profile.JournalMode = SqliteJournalMode.Wal;
    profile.Synchronous = SqliteSynchronousMode.Full;
    profile.BeginMode = SqliteBeginMode.Immediate;
    profile.ForeignKeys = true;
    profile.BusyTimeout = TimeSpan.FromSeconds(5);
});
#endif
await using var source = builder.Build();
if (options.Initialize)
{
    await Dataset.Initialize(source, options);
    return 0;
}
await using (var validation = await source.OpenSessionAsync())
{
    var tenants = await validation.ScalarAsync(SqlCommand.Create("SELECT tenants FROM db_perf_metadata WHERE schema_version=1"), Codecs.Int64);
    var count = await validation.ScalarAsync(SqlCommand.Create("SELECT orders_per_tenant FROM db_perf_metadata WHERE schema_version=1"), Codecs.Int64);
    if (tenants != options.Tenants || count != options.OrdersPerTenant) throw new InvalidOperationException("Dataset metadata does not match requested workload.");
}
using var native = new NativeDiagnostics(source.Provider);
if(options.RecoveryChecks)
{
    RecoveryOwners ReadOwners()
    {
        var state=native.Read();
        return new(checked((long)state.Sources),checked((long)state.Sessions),checked((long)state.Operations),
            checked((long)state.Cursors),checked((long)state.Buffers),checked((long)state.Owners),
            checked((long)state.SqlExecutions),checked((long)state.ProtocolSubmissions),"Korda native source/session/operation/cursor/buffer/owner counters");
    }
    var version=typeof(DataSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]??"unknown";
    var recovery=await RecoveryMeasurement.Run(options,"korda-public-fluent-recovery",source.Provider.ToString(),version,
        native.Path,native.Sha256,()=>Worker.Create(source,options),ReadOwners,source.DisposeAsync,static(worker,phase)=>worker.SetPhase(phase));
    var recoveryJson=JsonSerializer.Serialize(recovery,PerfJson.Default.RecoveryReceipt);
    if(options.Output is {} path)await File.WriteAllTextAsync(path,recoveryJson+Environment.NewLine);
    Console.WriteLine("DB_PERF_RECOVERY "+recoveryJson);
    return recovery.Passed?0:1;
}
if(options.LifecycleChecks)
{
    var lifecycle=await LifecycleVerification.Run(source,options,native);
    var lifecycleJson=JsonSerializer.Serialize(lifecycle,PerfJson.Default.LifecycleReceipt);
    if(options.Output is {} path)await File.WriteAllTextAsync(path,lifecycleJson+Environment.NewLine);
    Console.WriteLine("DB_PERF_LIFECYCLE "+lifecycleJson);
    return lifecycle.Checks.All(check=>check.Status=="passed")?0:1;
}
var receipt = await Measurement.Run(source, options, native);
var json = JsonSerializer.Serialize(receipt, PerfJson.Default.RunReceipt);
if (options.Output is { } output) await File.WriteAllTextAsync(output, json + Environment.NewLine);
Console.WriteLine("DB_PERF_RESULT " + json);
return receipt.Failed == 0 && receipt.OwnersReturnedToZero && receipt.NativeAllocationProfile?.Passed != false ? 0 : 1;
