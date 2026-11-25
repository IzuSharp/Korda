using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

if (RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("Run the published Linux Native AOT executable.");
if (args.Length is < 2 or > 5)
    throw new ArgumentException("connection output.json [iterations=256] [windows=3] [initialize]");
var iterations = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 256;
var windows = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 3;
if (iterations is < 1 or > 100000 || windows is < 1 or > 30)
    throw new ArgumentOutOfRangeException(nameof(args));
var diagnostics = new FailureDiagnostics(args[1]);
var builder = new DataSourceBuilder().WithName("DB-Perf-01 same SQL phases")
    .ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxPreparedPlans = 16, MaxBatchRows = 32 });
#if POSTGRES
builder.UsePostgreSql(args[0], profile => profile.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
#else
builder.UseSqlite(args[0]);
#endif
await using var source = builder.Build();
diagnostics.Provider = source.Provider.ToString();
await using var session = await source.OpenSessionAsync();
using var identity = new NativeIdentity(source.Provider);
diagnostics.Identity = identity;
#if POSTGRES
var databaseVersion = await session.ScalarAsync(SqlCommand.Create("SELECT version()"), Codecs.String);
#else
var databaseVersion = await session.ScalarAsync(SqlCommand.Create("SELECT sqlite_version()"), Codecs.String);
#endif
diagnostics.DatabaseVersion = databaseVersion;
if (args.Length == 5 && args[4] == "initialize")
    await Initialize(session);
using var probe = new PhaseProbe(session, diagnostics);
var first = new Sample[3];
for (var lane = 0; lane < 3; lane++)
{
    diagnostics.Locate("first", lane, -1, 0);
    first[lane] = await probe.Run(lane, 1);
}
// SQL equality is checked independently of the measured operation windows.
// The raw template was rendered during setup; first executions below are not
// independent cold compiler/runtime measurements.
diagnostics.Locate("sql-verification", -1, -1, -1);
diagnostics.Stage = FailureStage.Verify;
probe.VerifySql();
const int warmup = 64;
for (var lane = 0; lane < 3; lane++)
    for (var i = 0; i < warmup; i++)
    {
        diagnostics.Locate("warmup", lane, -1, i);
        _ = await probe.Run(lane, 1 + i % 2);
    }
var measurements = new Window[windows * 3];
using var process = Process.GetCurrentProcess();
for (var window = 0; window < windows; window++)
for (var position = 0; position < 3; position++)
{
    // Rotate route order across windows; each route uses the same single live
    // physical session and alternates exactly the same two parameter values.
    var lane = (window + position) % 3;
    var samples = new Sample[iterations];
    var nativeBefore = identity.Read();
    var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
    var cpuBefore = process.TotalProcessorTime.Ticks;
    var allocatedBefore = GC.GetTotalAllocatedBytes(true);
    var started = Stopwatch.GetTimestamp();
    for (var i = 0; i < iterations; i++)
    {
        diagnostics.Locate("window", lane, window, i);
        samples[i] = await probe.Run(lane, 1 + i % 2);
    }
    var ended = Stopwatch.GetTimestamp();
    var allocatedAfter = GC.GetTotalAllocatedBytes(true);
    var cpuAfter = process.TotalProcessorTime.Ticks;
    var end0 = GC.CollectionCount(0); var end1 = GC.CollectionCount(1); var end2 = GC.CollectionCount(2);
    process.Refresh();
    measurements[window * 3 + position] = new(PhaseProbe.Names[lane], window, iterations,
        allocatedBefore, allocatedAfter, end0 - gc0, end1 - gc1, end2 - gc2,
        cpuBefore, cpuAfter, process.WorkingSet64, process.PeakWorkingSet64,
        started, ended, nativeBefore, identity.Read(), samples);
    diagnostics.CompletedWindows++;
}
var prepareTicks = probe.PrepareTicks;
var sql = probe.Sql;
var rawTemplate = probe.RawTemplate;
diagnostics.Locate("teardown", -1, -1, -1);
diagnostics.Stage = FailureStage.Release;
var teardownStarted = Stopwatch.GetTimestamp();
probe.Dispose();
await session.DisposeAsync();
await source.DisposeAsync();
var teardownEnded = Stopwatch.GetTimestamp();
var drained = identity.Read();
if (drained is not { Sources: 0, Sessions: 0, Operations: 0, Cursors: 0, Buffers: 0, Owners: 0 })
    throw new InvalidOperationException("Native owners did not drain after explicit disposal.");
var receipt = new Receipt(2, source.Provider.ToString(), true, databaseVersion,
    typeof(DataSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
    NativeIdentity.Hash(Environment.ProcessPath!), identity.Path, identity.Sha256, sql, rawTemplate,
    "Int64 tenant alternates 1 and 2; ordered full List<PhaseRow> with 100 rows, exact Id/Note/null checks",
    Stopwatch.Frequency, prepareTicks, warmup, first, measurements,
    teardownEnded - teardownStarted, drained,
    "Local phase diagnostic. Construction includes the route's query/parameter/stream construction and result List. " +
    "Submit-to-first-row includes GetAsyncEnumerator/first MoveNextAsync/first DTO mapping. Read-complete includes all remaining rows and EOF. " +
    "Release includes enumerator retirement and dynamic query disposal. All routes materialize and verify the complete List with the same mapper. " +
    "Fluent and prepared SQL text are identical. SQLite raw SQL uses ?1 where Fluent uses ?; both bind the same sole ordinal-one value. PostgreSQL uses $1 in both. " +
    "Preparation and first execution are reported separately; SQL rendering and shared metadata setup precede the first route executions, which are not independent process-cold measurements. " +
    "Window allocation and GC are whole process; phase samples are timestamp durations, not thread-local allocation attribution. " +
    "CPU/RSS are local process observations. Protocol submissions are not physical network-round-trip measurements. No hosted-runner identity is required. " +
    "Failure diagnostics add scalar progress/stage assignments within the existing windows and phase boundaries; no per-operation logging or diagnostic counter reads occur on success.");
diagnostics.Locate("receipt", -1, -1, -1);
diagnostics.Stage = FailureStage.Receipt;
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(receipt, ProbeJson.Default.Receipt) + Environment.NewLine);
Console.WriteLine("DB_PERF_QUERY_PHASES " + args[1]);

static async Task Initialize(DataSession session)
{
    await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE query_phases (\"TenantId\" BIGINT NOT NULL, \"Id\" BIGINT NOT NULL, \"Note\" TEXT NULL, PRIMARY KEY(\"TenantId\",\"Id\"))", kind: SqlCommandKind.Mutation));
    await using var transaction = await session.BeginTransactionAsync();
    for (var tenant = 1; tenant <= 2; tenant++)
    {
        var sql = new StringBuilder("INSERT INTO query_phases VALUES ");
        for (var id = 1; id <= 100; id++)
        {
            if (id > 1) sql.Append(',');
            sql.Append('(').Append(tenant).Append(',').Append(id).Append(',');
            if (id % 5 == 0) sql.Append("NULL");
            else sql.Append("'note-").Append(tenant).Append('-').Append(id).Append('\'');
            sql.Append(')');
        }
        await session.ExecuteAsync(SqlCommand.Create(sql.ToString(), kind: SqlCommandKind.Mutation));
    }
    await transaction.CommitAsync();
    await transaction.DisposeAsync();
    await session.ExecuteAsync(SqlCommand.Create("ANALYZE query_phases", kind: SqlCommandKind.Mutation));
}

internal sealed class PhaseProbe : IDisposable
{
    internal static readonly string[] Names = ["fluent", "prepared", "host-sql"];
    private readonly DataSession _session;
    private readonly PreparedQuery<PhaseRow> _prepared;
    private readonly QueryParameter<long> _tenant;
    private readonly QueryParameters _values = new();
    private readonly FailureDiagnostics _diagnostics;
    private readonly string?[][] _notes = [new string?[100], new string?[100]];
    internal long PrepareTicks { get; }
    internal string Sql { get; private set; } = "";
    internal string RawTemplate { get; private set; } = "";

    internal PhaseProbe(DataSession session, FailureDiagnostics diagnostics)
    {
        _session = session; _diagnostics = diagnostics;
        for (var tenant = 1; tenant <= 2; tenant++)
        for (var id = 1; id <= 100; id++)
            _notes[tenant - 1][id - 1] = id % 5 == 0 ? null : $"note-{tenant}-{id}";
        var started = Stopwatch.GetTimestamp();
        using var query = session.From(PhaseOrders.Table);
        _tenant = query.Parameter("tenant", Codecs.Int64);
        query.Where(query.Column(PhaseOrders.TenantId).Equal(query.ParameterValue(_tenant))).OrderBy(PhaseOrders.Id);
        _prepared = Select(query).Prepare();
        PrepareTicks = Stopwatch.GetTimestamp() - started;
        // The trusted SQL template uses the shape emitted by this same native
        // dialect; VerifySql checks dynamic and parameterized shapes. SQLite's
        // raw-template compiler numbers its marker (?1) whereas Fluent emits
        // an anonymous marker (?). Both bind the sole value at ordinal one.
        using var rendered = Build(1);
        Sql = Select(rendered).ToSql();
        var marker = Sql.Contains("$1", StringComparison.Ordinal) ? "$1" : "?";
        if (!Sql.Contains(marker, StringComparison.Ordinal) || Sql.Contains("$2", StringComparison.Ordinal) || Sql.Split(marker, StringSplitOptions.None).Length != 2)
            throw new InvalidOperationException("Expected one ordered provider parameter in the observed SQL: " + Sql);
        RawTemplate = Sql.Replace(marker, "@tenant", StringComparison.Ordinal);
    }

    private Query<PhaseOrder> Build(long tenant)
        => _session.From(PhaseOrders.Table).WhereEqual(PhaseOrders.TenantId, tenant).OrderBy(PhaseOrders.Id);
    private static QuerySelection<PhaseRow> Select(Query<PhaseOrder> query)
        => query.Select(PhaseRows.Bind(query, Id: query.Column(PhaseOrders.Id), Note: query.Column(PhaseOrders.Note)));

    internal void VerifySql()
    {
        using var dynamicQuery = Build(2);
        if (Select(dynamicQuery).ToSql() != Sql) throw new InvalidOperationException("Values changed the dynamic SQL shape.");
        using var preparedQuery = _session.From(PhaseOrders.Table);
        var parameter = preparedQuery.Parameter("tenant", Codecs.Int64);
        preparedQuery.Where(preparedQuery.Column(PhaseOrders.TenantId).Equal(preparedQuery.ParameterValue(parameter))).OrderBy(PhaseOrders.Id);
        if (Select(preparedQuery).ToSql() != Sql) throw new InvalidOperationException("Prepared and dynamic SQL differ.");
    }

    internal async ValueTask<Sample> Run(int lane, long tenant)
    {
        List<PhaseRow>? rows = null;
        try
        {
            _diagnostics.Stage = FailureStage.Construct;
            Query<PhaseOrder>? query = null;
            IAsyncEnumerator<PhaseRow>? reader = null;
            var started = Stopwatch.GetTimestamp();
            rows = new List<PhaseRow>(100);
            IAsyncEnumerable<PhaseRow> stream;
            if (lane == 0)
            {
                query = Build(tenant);
                stream = Select(query).AsAsyncEnumerable();
            }
            else if (lane == 1)
                stream = _prepared.AsAsyncEnumerable(_session, _values.Set(_tenant, tenant));
            else if (lane == 2)
                stream = _session.StreamAsync(SqlCommand.Create(RawTemplate, new SqlParameters().Add("tenant", tenant, Codecs.Int64)), PhaseRows.Row);
            else throw new ArgumentOutOfRangeException(nameof(lane));
            var constructed = Stopwatch.GetTimestamp();
            long first = constructed, complete = constructed, released;
            try
            {
                _diagnostics.Stage = FailureStage.First;
                reader = stream.GetAsyncEnumerator();
                if (!await reader.MoveNextAsync()) throw new InvalidOperationException("Expected the first row.");
                rows.Add(reader.Current);
                first = Stopwatch.GetTimestamp();
                _diagnostics.Stage = FailureStage.Read;
                while (await reader.MoveNextAsync()) rows.Add(reader.Current);
                complete = Stopwatch.GetTimestamp();
            }
            catch (Exception failure)
            {
                // Capture the business failure before finally can expose a distinct
                // disposal failure. Rethrowing and disposal precedence are unchanged.
                _diagnostics.Capture(failure, rows.Count, "query");
                throw;
            }
            finally
            {
                _diagnostics.Stage = FailureStage.Release;
                if (reader is not null) await reader.DisposeAsync();
                query?.Dispose();
                released = Stopwatch.GetTimestamp();
            }
            // Verify the complete result outside phase times, but within every
            // whole-process window so its equal diagnostic overhead stays visible.
            _diagnostics.Stage = FailureStage.Verify;
            if (rows.Count != 100) throw new InvalidOperationException("Incomplete result.");
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].Id != i + 1 || rows[i].Note != _notes[tenant - 1][i])
                    throw new InvalidOperationException("Result field or null differs.");
            GC.KeepAlive(rows);
            return new(constructed - started, first - constructed, complete - first, released - complete, rows.Count);
        }
        catch (Exception failure)
        {
            _diagnostics.Capture(failure, rows?.Count, "run");
            throw;
        }
    }

    public void Dispose() => _prepared.Dispose();
}

[DbTable("query_phases", DescriptorName = "PhaseOrders")]
internal sealed record PhaseOrder([property: DbKey] long TenantId, [property: DbKey] long Id, string? Note);
[DbProjection(DescriptorName = "PhaseRows")]
internal sealed record PhaseRow(long Id, string? Note);
internal readonly record struct Counts(ulong Sources, ulong Sessions, ulong Operations, ulong Cursors, ulong Buffers, ulong Owners, ulong SqlExecutions, ulong ProtocolSubmissions);
internal readonly record struct Sample(long ConstructTicks, long SubmitToFirstRowTicks, long ReadCompleteTicks, long ReleaseTicks, int Rows);
internal sealed record Window(string Route, int Index, int Operations, long AllocatedBefore, long AllocatedAfter, int Gen0, int Gen1, int Gen2,
    long CpuBeforeTicks, long CpuAfterTicks, long ResidentBytes, long PeakResidentBytes, long StartedTicks, long FinishedTicks,
    Counts NativeBefore, Counts NativeAfter, Sample[] Samples);
internal sealed record Receipt(int SchemaVersion, string Provider, bool NativeAot, string DatabaseVersion, string PackageVersion, string ExecutableSha256,
    string NativePath, string NativeSha256, string Sql, string RawTemplate, string ParametersAndResults, long StopwatchFrequency,
    long PreparationTicks, int WarmupPerRoute, Sample[] FirstExecutions, Window[] Windows, long TeardownTicks, Counts Drained, string Scope);
[JsonSerializable(typeof(Receipt))]
[JsonSerializable(typeof(FailureReceipt))]
internal partial class ProbeJson : JsonSerializerContext { }

internal enum FailureStage { Setup, Construct, First, Read, Release, Verify, Receipt }
internal sealed record FailureError(string Type, int HResult, string? DatabaseKind, string? ProviderCode,
    ulong? CorrelationId, bool? IsTransient, string? StackTrace, FailureError[] Inner);
internal sealed record FailureObservation(string Boundary, string Area, string? Route, int Window, int Iteration,
    string Stage, int CompletedWindows, int? RowsConsumed, long ObservedTicks,
    Counts? NativeCounts, string? NativeCountsUnavailable, FailureError Error);
internal sealed record FailureReceipt(int SchemaVersion, int SuccessReceiptSchemaVersion, string Provider,
    string? DatabaseVersion, string PackageVersion, string ExecutableSha256, string? NativeSha256,
    long StopwatchFrequency, bool IsTerminating, bool TimingCredit, int DroppedObservations,
    string? CaptureErrorType, FailureObservation[] Observations, string Scope);

internal sealed class FailureDiagnostics
{
    private const int MaximumObservations = 8;
    private readonly string _output;
    private readonly object _gate = new();
    private readonly Exception?[] _exceptions = new Exception?[MaximumObservations];
    private readonly FailureObservation?[] _observations = new FailureObservation?[MaximumObservations];
    private int _count, _dropped, _reported;
    private string? _captureError;
    private string _area = "setup";
    private int _lane = -1, _window = -1, _iteration = -1;
    internal FailureStage Stage;
    internal int CompletedWindows;
    internal string Provider = "unavailable";
    internal string? DatabaseVersion;
    internal NativeIdentity? Identity;

    internal FailureDiagnostics(string output)
    {
        _output = output + ".failure.json";
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
    }

    // The steady path only writes scalar progress. The query itself is serial;
    // a background-thread failure reports the last observed probe location.
    internal void Locate(string area, int lane, int window, int iteration)
    { _area = area; _lane = lane; _window = window; _iteration = iteration; }

    internal void Capture(Exception failure, int? rows, string boundary)
    {
        try
        {
            lock (_gate)
            {
                for (var i = 0; i < _count; i++)
                    if (ReferenceEquals(_exceptions[i], failure)) return;
                if (_count == MaximumObservations) { _dropped++; return; }
                Counts? counts = null;
                string? unavailable = "native identity not yet available";
                if (Identity is { } identity)
                {
                    if (identity.TryRead(out var available, out unavailable)) counts = available;
                }
                var observation = new FailureObservation(boundary, _area,
                    _lane is >= 0 and < 3 ? PhaseProbe.Names[_lane] : null, _window, _iteration,
                    Stage.ToString(), CompletedWindows, rows, Stopwatch.GetTimestamp(), counts, unavailable,
                    Describe(failure));
                _exceptions[_count] = failure;
                _observations[_count++] = observation;
            }
        }
        catch (Exception captureFailure)
        {
            // A diagnostic failure must never replace the original exception.
            _captureError = captureFailure.GetType().FullName;
        }
    }

    private static FailureError Describe(Exception failure, int depth = 0)
    {
        var database = failure as DatabaseException;
        FailureError[] inner = depth >= 4 ? [] : failure is AggregateException aggregate
            ? aggregate.InnerExceptions.Take(MaximumObservations).Select(error => Describe(error, depth + 1)).ToArray()
            : failure.InnerException is { } cause ? new[] { Describe(cause, depth + 1) } : [];
        // Do not serialize Message, Data, arguments or environment: arbitrary
        // exception messages can contain connection strings or parameter values.
        return new(failure.GetType().FullName ?? failure.GetType().Name, failure.HResult,
            database?.Kind.ToString(), database?.ProviderCode, database?.CorrelationId, database?.IsTransient,
            failure.StackTrace, inner);
    }

    private void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0) return;
        try
        {
            if (args.ExceptionObject is Exception failure) Capture(failure, null, "unhandled");
            FailureObservation[] observations;
            lock (_gate) observations = _observations.Take(_count).Select(value => value!).ToArray();
            var receipt = new FailureReceipt(1, 2, Provider, DatabaseVersion,
                typeof(DataSource).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                NativeIdentity.Hash(Environment.ProcessPath!), Identity?.Sha256, Stopwatch.Frequency,
                args.IsTerminating, false, _dropped, _captureError, observations,
                "Failure-only diagnostics; no partial performance acceptance. Window/iteration are zero-based; -1 means not in a measured window. " +
                "Query/run observations precede unwind; an unhandled-only observation carries the last probe location. " +
                "Owner counters are point observations, not fabricated final retirement. Original exception propagation and disposal precedence are unchanged. " +
                "Exception Message/Data, connection strings, parameters and environment are deliberately absent. Inner exceptions are bounded to depth four/eight children.");
            using var file = new FileStream(_output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, receipt, ProbeJson.Default.FailureReceipt);
            Console.Error.WriteLine("DB_PERF_QUERY_PHASE_FAILURE " + _output);
        }
        catch (Exception reportingFailure)
        {
            try { Console.Error.WriteLine("DB_PERF_QUERY_PHASE_FAILURE_RECORD_ERROR " + reportingFailure.GetType().FullName); }
            catch (Exception) { /* A closed diagnostic sink must not throw from the unhandled notification. */ }
        }
        // UnhandledException notification does not handle or retry the failure.
    }
}

internal sealed class NativeIdentity : IDisposable
{
    private readonly nint _module, _count;
    private readonly object _lifetime = new();
    private bool _disposed;
    internal string Path { get; }
    internal string Sha256 { get; }
    internal NativeIdentity(DatabaseProvider provider)
    {
        var name = provider == DatabaseProvider.PostgreSql ? "libnw_database_postgresql.so" : "libnw_database_sqlite.so";
        var paths = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 6 && System.IO.Path.GetFileName(parts[5]) == name).Select(parts => parts[5]).Distinct().ToArray();
        if (paths.Length != 1) throw new InvalidOperationException("Expected one loaded native provider.");
        Path = paths[0]; Sha256 = Hash(Path);
        _module = NativeLibrary.Load(Path);
        _count = NativeLibrary.GetExport(_module, "nw_db_diagnostic_count_v1");
    }
    internal static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    internal unsafe Counts Read()
    { var count = (delegate* unmanaged[Cdecl]<uint, ulong>)_count; return new(count(0), count(1), count(2), count(3), count(4), count(5), count(6), count(7)); }
    internal bool TryRead(out Counts counts, out string? unavailable)
    {
        lock (_lifetime)
        {
            counts = default;
            if (_disposed) { unavailable = "native diagnostic module already disposed"; return false; }
            try { counts = Read(); unavailable = null; return true; }
            catch (Exception failure) { unavailable = failure.GetType().FullName; return false; }
        }
    }
    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            NativeLibrary.Free(_module);
        }
    }
}
