using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DbPerf.Campaign;
using Korda.Data;
using Korda.Database;
#if POSTGRES
using Korda.Database.PostgreSql;
#else
using Korda.Database.Sqlite;
#endif

if(RuntimeFeature.IsDynamicCodeSupported)throw new InvalidOperationException("The campaign requires Native AOT.");
#if POSTGRES
const string provider="postgresql";
#else
const string provider="sqlite";
#endif
bool lifecycle=args.Contains("--lifecycle-probe",StringComparer.Ordinal);
var config=Config.Parse(lifecycle?args.Where(arg=>arg!="--lifecycle-probe").ToArray():args,provider);
var builder=new DataSourceBuilder().WithName("DB-Perf-01 campaign").ConfigureLimits(new DatabaseLimits
{
    MaxConnections=config.Concurrency,MaxWaiters=Math.Max(16,config.Concurrency*2),
    AdmissionTimeout=TimeSpan.FromSeconds(5),CommandTimeout=TimeSpan.FromSeconds(5),
    MaxBatchRows=config.BatchRows,MaxBatchBytes=1024*1024,MaxFieldBytes=4096,
    MaxBufferedRows=100_000,MaxBufferedBytes=256L*1024*1024,
    MaxPreparedPlans=128,MaxPreparedPlanBytes=8L*1024*1024,
});
#if POSTGRES
builder.UsePostgreSql(config.Connection,profile=>profile.TlsMode=PostgreSqlTlsMode.DisableForLocalDevelopment);
#else
builder.UseSqlite(config.Connection,profile=>
{
    profile.JournalMode=SqliteJournalMode.Wal;profile.Synchronous=SqliteSynchronousMode.Full;
    profile.ForeignKeys=true;profile.BusyTimeout=TimeSpan.FromSeconds(5);
});
#endif
await using var source=builder.Build();
var options=KordaWorker.ToOptions(config);
if(config.Initialize){await Dataset.Initialize(source,options);return 0;}
await using(var validation=await source.OpenSessionAsync())
{
    var tenants=await validation.ScalarAsync(SqlCommand.Create("SELECT tenants FROM db_perf_metadata WHERE schema_version=1"),Codecs.Int64);
    var orders=await validation.ScalarAsync(SqlCommand.Create("SELECT orders_per_tenant FROM db_perf_metadata WHERE schema_version=1"),Codecs.Int64);
    if(tenants!=config.Tenants||orders!=config.OrdersPerTenant)throw new InvalidOperationException("Dataset metadata differs from the frozen contract.");
}
using var native=new NativeDiagnostics(source.Provider);
if(lifecycle)return await LifecycleProbe.RunAsync(source,config,native);
if(config.ExportOnly)
{
    await using var worker=await KordaWorker.CreateAsync(source,config,0);
    return 0;
}
var version=typeof(DataSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"unknown";
var info=new AdapterInfo("korda",provider,version,
    "dynamic Fluent construction or reused immutable native plan; provider physical statements may be re-prepared; raw command and relation reuse are separately declared",
    config.SessionMode,[native.Path]);
return await Runner.RunAsync(config,async id=>await KordaWorker.CreateAsync(source,config,id),info,
    async ()=>{await source.DisposeAsync();return JsonSerializer.SerializeToElement(native.Read(),PerfJson.Default.NativeCounts);},
    ()=>JsonSerializer.SerializeToElement(native.Read(),PerfJson.Default.NativeCounts));
