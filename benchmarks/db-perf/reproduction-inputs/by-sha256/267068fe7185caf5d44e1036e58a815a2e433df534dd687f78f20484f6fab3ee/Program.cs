using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korda.Data;
using Korda.Database;
using Korda.Query;
#if POSTGRES
using Korda.Database.PostgreSql;
#else
using Korda.Database.Sqlite;
#endif

if(RuntimeFeature.IsDynamicCodeSupported)throw new InvalidOperationException("Published Native AOT required.");
if(args.Length is <2 or >4)throw new ArgumentException("connection output.json [iterations=100000] [windows=9]");
var iterations=args.Length>2?int.Parse(args[2],CultureInfo.InvariantCulture):100000;
var windows=args.Length>3?int.Parse(args[3],CultureInfo.InvariantCulture):9;
if(iterations is <1 or >1000000||windows is <1 or >30)throw new ArgumentOutOfRangeException(nameof(args));
var builder=new DataSourceBuilder().WithName("DB-Perf-01 prepared layout probe")
    .ConfigureLimits(new DatabaseLimits{MaxConnections=2,MaxPreparedPlans=32});
#if POSTGRES
builder.UsePostgreSql(args[0],profile=>profile.TlsMode=PostgreSqlTlsMode.DisableForLocalDevelopment);
#else
builder.UseSqlite(args[0]);
#endif
await using var source=builder.Build();
await using var session=await source.OpenSessionAsync();
using var native=new NativeIdentity(source.Provider);
using var zeroQuery=session.From(ProbeOrders.Table).WhereEqual(ProbeOrders.TenantId,1L).OrderBy(ProbeOrders.Id).Take(2);
using var zero=zeroQuery.Select(zeroQuery.Column(ProbeOrders.Id)).Prepare();
using var oneQuery=session.From(ProbeOrders.Table);
var minimum=oneQuery.Parameter("minimum",Codecs.Int64);
oneQuery.WhereEqual(ProbeOrders.TenantId,1L).Where(oneQuery.Column(ProbeOrders.Id).GreaterThanOrEqual(oneQuery.ParameterValue(minimum)))
    .OrderBy(ProbeOrders.Id).Take(2);
using var one=oneQuery.Select(oneQuery.Column(ProbeOrders.Id)).Prepare();
var values=new QueryParameters().Set(minimum,1L);var empty=new QueryParameters();
if(!(await zero.ToListAsync(session)).SequenceEqual([1L,2L]))throw new InvalidOperationException("Zero-parameter plan result differs.");
var snapshot=one.AsAsyncEnumerable(session,values);values.Set(minimum,2L);
var captured=new List<long>();await foreach(var value in snapshot)captured.Add(value);
if(!captured.SequenceEqual([1L,2L])||!(await one.ToListAsync(session,values)).SequenceEqual([2L,3L]))
    throw new InvalidOperationException("Parameter values were not independently captured before enumeration.");
values.Set(minimum,1L);
var names=new[]{"empty-driver","parameterless-null","parameterless-empty-values","one-int64-snapshot","one-int64-bind-and-snapshot"};
Func<IAsyncEnumerable<long>?>[] factories=[()=>null,()=>zero.AsAsyncEnumerable(session),()=>zero.AsAsyncEnumerable(session,empty),
    ()=>one.AsAsyncEnumerable(session,values),()=>one.AsAsyncEnumerable(session,values.Set(minimum,1L))];
foreach(var factory in factories)Run(factory,10000);
GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
var results=new Window[windows*names.Length];var before=native.Read();
for(var window=0;window<windows;window++)
for(var lane=0;lane<names.Length;lane++)
{
    var before0=GC.CollectionCount(0);var before1=GC.CollectionCount(1);var before2=GC.CollectionCount(2);
    var beforeBytes=GC.GetTotalAllocatedBytes(true);var started=Stopwatch.GetTimestamp();
    Run(factories[lane],iterations);
    var ended=Stopwatch.GetTimestamp();var afterBytes=GC.GetTotalAllocatedBytes(true);
    results[window*names.Length+lane]=new(names[lane],window,iterations,beforeBytes,afterBytes,afterBytes-beforeBytes,
        GC.CollectionCount(0)-before0,GC.CollectionCount(1)-before1,GC.CollectionCount(2)-before2,started,ended);
}
var after=native.Read();
if(before!=after)throw new InvalidOperationException("Lazy construction dispatched or changed native ownership.");
one.Dispose();zero.Dispose();oneQuery.Dispose();zeroQuery.Dispose();
await session.DisposeAsync();await source.DisposeAsync();var drained=native.Read();
if(drained is not{Sources:0,Sessions:0,Operations:0,Cursors:0,Buffers:0,Owners:0})
    throw new InvalidOperationException("Prepared probe did not drain all native owners.");
var executable=Environment.ProcessPath??throw new InvalidOperationException("Executable identity unavailable.");
var receipt=new Receipt(1,source.Provider.ToString(),true,
    typeof(DataSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"unknown",
    Hash(executable),native.Path,native.Sha256,Stopwatch.Frequency,10000,iterations,windows,before,after,drained,results,
    "Public PreparedQuery.AsAsyncEnumerable construction only; no enumeration/SQL dispatch inside measured windows. " +
    "Whole-process precise cumulative allocation counters cover all threads; empty driver is separate, not subtracted. " +
    "All result values remain unmaterialized; any measured allocation belongs to framework/lazy execution construction or diagnostic background activity. " +
    "This boundary does not establish complete query execution or async I/O zero allocation. Two retained prepared plans bound cache capacity.");
var json=JsonSerializer.Serialize(receipt,ProbeJson.Default.Receipt);await File.WriteAllTextAsync(args[1],json+Environment.NewLine);
Console.WriteLine("DB_PERF_PREPARED_LAYOUT "+json);

static void Run(Func<IAsyncEnumerable<long>?> factory,int count)
{for(var i=0;i<count;i++)GC.KeepAlive(factory());}
static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexStringLower(SHA256.HashData(file));}

[DbTable("orders",DescriptorName="ProbeOrders")]
internal sealed record ProbeOrder([property:DbKey]long TenantId,[property:DbKey]long Id);
internal readonly record struct Counts(ulong Sources,ulong Sessions,ulong Operations,ulong Cursors,ulong Buffers,ulong Owners,ulong SqlExecutions,ulong ProtocolSubmissions);
internal readonly record struct Window(string Lane,int Index,int Operations,long AllocatedBefore,long AllocatedAfter,long AllocatedBytes,
    int Gen0,int Gen1,int Gen2,long StartedTicks,long FinishedTicks);
internal sealed record Receipt(int SchemaVersion,string Provider,bool NativeAot,string PackageVersion,string ExecutableSha256,
    string NativePath,string NativeSha256,long StopwatchFrequency,int WarmupPerLane,int Iterations,int Windows,
    Counts Before,Counts After,Counts Drained,Window[] Measurements,string Scope);
[JsonSerializable(typeof(Receipt))]
internal partial class ProbeJson:JsonSerializerContext { }
internal sealed class NativeIdentity:IDisposable
{
    private readonly nint _module,_count;
    internal string Path{get;}
    internal string Sha256{get;}
    internal NativeIdentity(DatabaseProvider provider)
    {
        var name=provider==DatabaseProvider.PostgreSql?"libnw_database_postgresql.so":"libnw_database_sqlite.so";
        var files=File.ReadLines("/proc/self/maps").Select(line=>line.Split(' ',6,StringSplitOptions.RemoveEmptyEntries))
            .Where(parts=>parts.Length==6&&System.IO.Path.GetFileName(parts[5])==name).Select(parts=>parts[5]).Distinct().ToArray();
        if(files.Length!=1)throw new InvalidOperationException("Expected one loaded native provider image.");
        Path=files[0];using(var file=File.OpenRead(Path))Sha256=Convert.ToHexStringLower(SHA256.HashData(file));
        _module=NativeLibrary.Load(Path);_count=NativeLibrary.GetExport(_module,"nw_db_diagnostic_count_v1");
    }
    internal unsafe Counts Read()
    {var count=(delegate*unmanaged[Cdecl]<uint,ulong>)_count;return new(count(0),count(1),count(2),count(3),count(4),count(5),count(6),count(7));}
    public void Dispose()=>NativeLibrary.Free(_module);
}
