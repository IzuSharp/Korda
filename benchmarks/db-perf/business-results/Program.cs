using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
#if KORDA
using Korda.Data;
using Korda.Database;
using Korda.Query;
#if POSTGRES
using Korda.Database.PostgreSql;
#else
using Korda.Database.Sqlite;
#endif
#else
using System.Data.Common;
using Dapper;
#if POSTGRES
using ProviderConnection = Npgsql.NpgsqlConnection;
#else
using ProviderConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif
[module: DapperAot]
#endif

internal static class Program
{
    internal const int ResultRows = 100;
    private const int WarmupOperations = 64, Windows = 3, CloneOperations = 1024;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length is < 5 or > 6)
            throw new ArgumentException("Usage: BusinessResults <fresh-database> <result.json> <primary.nupkg> <source-label> <sqlite-library-or-dash> [operations-per-window=128]");
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Run the standalone Native AOT ELF.");
        string output = Path.GetFullPath(args[1]);
        if (File.Exists(output)) throw new IOException("Never replace an earlier measurement.");
        int operations = args.Length == 6 ? int.Parse(args[5], CultureInfo.InvariantCulture) : 128;
        if (operations is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(operations));
        var report = new Report
        {
            Route = Driver.Route, Provider = Driver.Provider, SourceLabel = args[3],
            PrimaryPackagePath = Path.GetFullPath(args[2]), PrimaryPackageSha256 = Hash(args[2]),
            ExecutableSha256 = Hash(Environment.ProcessPath!), Runtime = RuntimeInformation.FrameworkDescription,
            NativeAot = true, ProcessId = Environment.ProcessId, StartedUtc = DateTimeOffset.UtcNow,
            WarmupOperations = WarmupOperations, OperationsPerWindow = operations
        };
        Driver? driver = null;
        List<BusinessRow>? template = null;
        // All reference values and expected strings are prepared before measurement.
        string?[] expected = Enumerable.Range(1, ResultRows).Select(Note).ToArray();
        try
        {
            string receiptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "build-receipt.json"));
            report.BuildReceiptPath = receiptPath;
            report.BuildReceiptSha256 = Hash(receiptPath);
            using (var metadata = JsonDocument.Parse(File.ReadAllBytes(receiptPath)))
            {
                var build = metadata.RootElement;
                if (build.GetProperty("route").GetString() != Driver.Route || build.GetProperty("provider").GetString() != Driver.Provider
                    || build.GetProperty("executable_sha256").GetString() != report.ExecutableSha256 || build.GetProperty("exit_code").GetInt32() != 0)
                    throw new InvalidOperationException("Actual executable differs from the external build receipt.");
                string primary = build.GetProperty("primary_package").GetString()! + "/";
                var restored = build.GetProperty("packages").EnumerateObject().Single(item => item.Name.StartsWith(primary, StringComparison.Ordinal));
                if (restored.Value.GetProperty("sha256").GetString() != report.PrimaryPackageSha256)
                    throw new InvalidOperationException("The supplied archive is not the actual primary package in the restore graph.");
                report.ConsumerSources = build.GetProperty("sources").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!);
            }
            _ = Counts.Read();
            driver = await Driver.Open(args[0], args[4]);
            report.NativeImages = driver.NativeImages;
            await driver.Initialize(expected);
            report.Sql = driver.Sql;
            report.Parameters = "Semantic values: tenant Int64(1), limit Int32(100). Dynamic construction/binding occurs per operation; SQL retains route-specific parameter names.";
            for (int i = 0; i < WarmupOperations; i++)
            {
                template = await driver.Query();
                _ = Consume(template, expected, capacity: null);
            }
            if (template is null) throw new InvalidOperationException("Missing real result template.");
            report.Graph = InspectGraph(template, expected);
            var sampleClone = Clone(template);
            CheckClone(template, sampleClone, expected);
            GC.KeepAlive(sampleClone);
            for (int window = 0; window < Windows; window++)
                report.EndToEnd.Add(await MeasureQuery(driver, expected, template.Capacity, operations, window));
            // The business control runs after database owners and pools are drained.
            // It recreates the actual retained graph; it is not an in-query trace.
            var completedDriver = driver;
            driver = null;
            await completedDriver.DisposeAsync();
            report.DatabaseDisposed = true;
            report.NativeOwnersAfterDrain = completedDriver.FinalOwners;
            report.NativeOwnerScope = completedDriver.OwnerScope;
            for (int i = 0; i < WarmupOperations; i++) GC.KeepAlive(Clone(template));
            for (int window = 0; window < Windows; window++)
                report.BusinessControls.Add(MeasureClone(template, expected, window));
            report.Passed = report.EndToEnd.Count == Windows && report.BusinessControls.Count == Windows;
            GC.KeepAlive(template);
        }
        catch (Exception error)
        {
            if (error is MeasurementException measured)
                (measured.Window.SynchronousControl ? report.BusinessControls : report.EndToEnd).Add(measured.Window);
            report.Error = error.GetType().FullName + ": " + error.Message;
        }
        finally
        {
            if (driver is not null)
            {
                try
                {
                    await driver.DisposeAsync();
                    report.DatabaseDisposed = true;
                    report.NativeOwnersAfterDrain = driver.FinalOwners;
                    report.NativeOwnerScope = driver.OwnerScope;
                }
                catch (Exception error) { report.CleanupError = error.GetType().FullName + ": " + error.Message; report.Passed = false; }
            }
            report.FinishedUtc = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, report, ProbeJson.Default.Report);
        }
        return report.Passed && report.Error is null && report.CleanupError is null ? 0 : 1;
    }

    private static async Task<AllocationWindow> MeasureQuery(Driver driver, string?[] expected, int capacity, int operations, int window)
    {
        int issued = 0, completed = 0, incomplete = 0, threadChanges = 0;
        long rows = 0;
        ulong digest = 14695981039346656037UL;
        List<BusinessRow>? last = null;
        Exception? failure = null;
        Counts before = Counts.Read();
        try
        {
            for (int i = 0; i < operations; i++)
            {
                issued++;
                int thread = Environment.CurrentManagedThreadId;
                var pending = driver.Query();
                if (!pending.IsCompleted) incomplete++;
                last = await pending.ConfigureAwait(false);
                if (thread != Environment.CurrentManagedThreadId) threadChanges++;
                ulong one = Consume(last, expected, capacity);
                digest = unchecked((digest ^ one) * 1099511628211UL);
                rows += last.Count;
                completed++;
            }
        }
        catch (Exception error) { failure = error; }
        Counts after = Counts.Read();
        GC.KeepAlive(last);
        var result = new AllocationWindow("end-to-end-" + window, before, after, operations, issued, completed,
            issued - completed, rows, digest, incomplete, threadChanges, false,
            failure is null ? null : failure.GetType().FullName + ": " + failure.Message);
        if (failure is not null) throw new MeasurementException(result, failure);
        return result;
    }

    private static AllocationWindow MeasureClone(List<BusinessRow> template, string?[] expected, int window)
    {
        List<BusinessRow>? last = null;
        ulong digest = 14695981039346656037UL;
        int issued = 0, completed = 0;
        Exception? failure = null;
        Counts before = Counts.Read();
        try
        {
            for (int i = 0; i < CloneOperations; i++)
            {
                issued++;
                last = Clone(template);
                digest = unchecked((digest ^ Consume(last, expected, template.Capacity)) * 1099511628211UL);
                completed++;
            }
        }
        catch (Exception error) { failure = error; }
        Counts after = Counts.Read();
        GC.KeepAlive(last);
        if (before.ThreadId != after.ThreadId) throw new InvalidOperationException("Synchronous business control changed threads.");
        var result = new AllocationWindow("business-graph-control-" + window, before, after, CloneOperations, issued,
            completed, issued - completed, (long)completed * ResultRows, digest, 0, 0, true,
            failure is null ? null : failure.GetType().FullName + ": " + failure.Message);
        if (failure is not null) throw new MeasurementException(result, failure);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<BusinessRow> Clone(List<BusinessRow> template)
    {
        var result = new List<BusinessRow>(template.Capacity);
        foreach (var row in template)
            result.Add(new BusinessRow(row.Id, row.Note is null ? null : new string(row.Note.AsSpan())));
        return result;
    }

    private static ulong Consume(List<BusinessRow> rows, string?[] expected, int? capacity)
    {
        if (rows.Count != ResultRows || capacity.HasValue && rows.Capacity != capacity.Value)
            throw new InvalidOperationException("Result count or observed List capacity changed.");
        ulong digest = 14695981039346656037UL;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Id != i + 1 || !string.Equals(row.Note, expected[i], StringComparison.Ordinal))
                throw new InvalidOperationException("Complete buffered result differs from the fixed data.");
            digest = unchecked((digest ^ (ulong)row.Id) * 1099511628211UL);
            if (row.Note is null) digest = unchecked((digest ^ 0xffUL) * 1099511628211UL);
            else foreach (char value in row.Note) digest = unchecked((digest ^ value) * 1099511628211UL);
        }
        return digest;
    }

    private static GraphShape InspectGraph(List<BusinessRow> rows, string?[] expected)
    {
        _ = Consume(rows, expected, capacity: null);
        var objects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        int strings = 0, nulls = 0, characters = 0;
        foreach (var row in rows)
        {
            if (!objects.Add(row)) throw new InvalidOperationException("Shared DTO identity requires a different graph control.");
            if (row.Note is null) { nulls++; continue; }
            if (row.Note.Length == 0 || ReferenceEquals(string.IsInterned(row.Note), row.Note) || !objects.Add(row.Note))
                throw new InvalidOperationException("The control requires independently allocated, non-interned, nonempty returned strings.");
            strings++; characters += row.Note.Length;
        }
        return new(rows.Count, rows.Capacity, strings, nulls, characters, Consume(rows, expected, null));
    }

    private static void CheckClone(List<BusinessRow> original, List<BusinessRow> clone, string?[] expected)
    {
        if (InspectGraph(original, expected) != InspectGraph(clone, expected) || ReferenceEquals(original, clone))
            throw new InvalidOperationException("Business control graph does not match the returned result.");
        for (int i = 0; i < original.Count; i++)
            if (ReferenceEquals(original[i], clone[i]) || original[i].Note is not null && ReferenceEquals(original[i].Note, clone[i].Note))
                throw new InvalidOperationException("Business control reused a required result object.");
    }

    internal static string? Note(int id) => id % 5 == 0 ? null : "note-Ω-" + (id % 16).ToString(CultureInfo.InvariantCulture);
    internal static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
}

internal sealed class Driver : IAsyncDisposable
{
#if KORDA
    internal const string Route = "korda";
    private readonly DataSource _source;
    private readonly DataSession _session;
    private readonly NativeCounts _native;
    private Driver(DataSource source, DataSession session)
    {
        _source = source; _session = session; _native = new(source.Provider);
        NativeImages = new() { [_native.Path] = Program.Hash(_native.Path) };
        using var query = _session.From(BusinessOrders.Table);
        query.WhereEqual(BusinessOrders.TenantId, 1L).OrderBy(BusinessOrders.Id).Take(Program.ResultRows);
        Sql = query.Select(BusinessRows.Bind(query, Id: query.Column(BusinessOrders.Id), Note: query.Column(BusinessOrders.Note))).ToSql();
    }
#else
    internal const string Route = "dapper";
    private readonly DbConnection _connection;
    private static readonly string SelectSql = "SELECT \"Id\" AS \"Id\", \"Note\" AS \"Note\" FROM business_result_orders ";
    private static readonly string FilterSql = "WHERE \"TenantId\" = @TenantId ORDER BY \"Id\" LIMIT @Limit";
    private Driver(DbConnection connection, Dictionary<string, string> images)
    {
        _connection = connection; NativeImages = images; Sql = string.Concat(SelectSql, FilterSql);
        if (ReferenceEquals(string.Concat(SelectSql, FilterSql), string.Concat(SelectSql, FilterSql)))
            throw new InvalidOperationException("Dynamic SQL construction unexpectedly reused a string.");
    }
#endif
#if POSTGRES
    internal const string Provider = "postgresql";
#else
    internal const string Provider = "sqlite";
#endif
    internal string Sql { get; }
    internal Dictionary<string, string> NativeImages { get; }
    internal ulong[]? FinalOwners { get; private set; }
    private bool _disposed;
    internal string OwnerScope => Route == "korda" ? "Six native diagnostic owners: sources,sessions,operations,cursors,buffers,handles; cumulative SQL/protocol counters follow."
        : "Application connection disposed and provider pools cleared; internal provider/native owners uninstrumented.";

    internal static async Task<Driver> Open(string connection, string sqliteLibrary)
    {
#if KORDA
        var builder = new DataSourceBuilder().WithName("DB-Perf-01-business-result-control")
            .ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxBatchRows = 128, MaxBufferedRows = 512,
                MaxBatchBytes = 65536, MaxFieldBytes = 1024, MaxBufferedBytes = 1048576 });
#if POSTGRES
        builder.UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
#else
        builder.UseSqlite(connection);
#endif
        var source = builder.Build();
        try { return new Driver(source, await source.OpenSessionAsync()); }
        catch { await source.DisposeAsync(); throw; }
#else
        var images = new Dictionary<string, string>();
#if POSTGRES
        var settings = connection.StartsWith("postgresql://", StringComparison.Ordinal)
            ? PostgreSqlSettings(new Uri(connection)) : new Npgsql.NpgsqlConnectionStringBuilder(connection);
        settings.Pooling = true; settings.MinPoolSize = 0; settings.MaxPoolSize = 1; settings.MaxAutoPrepare = 0;
        var session = new ProviderConnection(settings.ConnectionString);
#else
        if (sqliteLibrary == "-") throw new ArgumentException("Select the exact pinned SQLite shared library.");
        string path = Path.GetFullPath(sqliteLibrary);
        nint module = NativeLibrary.Load(path);
        // This provider image remains loaded for the process lifetime.
        NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
            (name, _, _) => name == "sqlite3" ? module : nint.Zero);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        string[] mapped = NativeCounts.Mapped("libsqlite3.so");
        if (mapped.Length != 1 || Path.GetFullPath(mapped[0]) != path)
            throw new InvalidOperationException("The selected SQLite image is not the sole mapped engine.");
        images[path] = Program.Hash(path);
        var session = new ProviderConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = connection, Pooling = true, DefaultTimeout = 5 }.ConnectionString);
#endif
        try
        {
            await session.OpenAsync();
#if !POSTGRES
            await session.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
#endif
            return new Driver(session, images);
        }
        catch { await session.DisposeAsync(); throw; }
#endif
    }

#if !KORDA && POSTGRES
    private static Npgsql.NpgsqlConnectionStringBuilder PostgreSqlSettings(Uri uri)
    {
        if (uri.UserInfo.Contains(':')) throw new ArgumentException("Use a provider connection string when credentials need escaping.");
        return new() { Host = uri.Host, Port = uri.Port < 0 ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(uri.UserInfo), Database = uri.AbsolutePath.Trim('/'), SslMode = Npgsql.SslMode.Disable };
    }
#endif

    internal async Task Initialize(string?[] expected)
    {
        // A dedicated empty database only. Existing tables are never dropped.
        await Execute("CREATE TABLE business_result_orders (\"TenantId\" BIGINT NOT NULL, \"Id\" BIGINT NOT NULL, \"Note\" TEXT NULL, PRIMARY KEY(\"TenantId\",\"Id\"))");
        var sql = new StringBuilder("INSERT INTO business_result_orders VALUES ");
        for (int i = 0; i < Program.ResultRows; i++)
        {
            if (i != 0) sql.Append(',');
            sql.Append("(1,").Append(i + 1).Append(',');
            if (expected[i] is null) sql.Append("NULL"); else sql.Append('\'').Append(expected[i]).Append('\'');
            sql.Append(')');
        }
        await Execute(sql.ToString());
    }

    private Task<long> Execute(string sql)
    {
#if KORDA
        return _session.ExecuteAsync(SqlCommand.Create(sql, kind: SqlCommandKind.Mutation));
#else
        return ExecuteDapper(sql);
#endif
    }
#if !KORDA
    private async Task<long> ExecuteDapper(string sql) => await _connection.ExecuteAsync(sql);
#endif

    internal async Task<List<BusinessRow>> Query()
    {
#if KORDA
        using var query = _session.From(BusinessOrders.Table);
        query.WhereEqual(BusinessOrders.TenantId, 1L).OrderBy(BusinessOrders.Id).Take(Program.ResultRows);
        var rows = await query.Select(BusinessRows.Bind(query, Id: query.Column(BusinessOrders.Id), Note: query.Column(BusinessOrders.Note)))
            .ToListAsync().ConfigureAwait(false);
        return rows as List<BusinessRow> ?? throw new InvalidOperationException("The public ToList result was not an actual List; no diagnostic copy is permitted.");
#else
        string sql = string.Concat(SelectSql, FilterSql);
        var rows = await _connection.QueryAsync<BusinessRow>(sql, new QueryArguments(1L, Program.ResultRows)).ConfigureAwait(false);
        var result = rows.AsList();
        if (!ReferenceEquals(rows, result)) throw new InvalidOperationException("AsList introduced a second materialization; retain this as an unsupported probe variant.");
        return result;
#endif
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
#if KORDA
        try
        {
            try { await _session.DisposeAsync(); }
            finally { await _source.DisposeAsync(); }
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            do
            {
                FinalOwners = _native.Read();
                if (FinalOwners.Take(6).All(value => value == 0)) return;
                Thread.Sleep(10);
            } while (Stopwatch.GetTimestamp() < deadline);
            throw new TimeoutException("Native owners did not drain.");
        }
        finally { _native.Dispose(); }
#else
        await _connection.DisposeAsync();
        if (_connection.State != System.Data.ConnectionState.Closed) throw new InvalidOperationException("Disposed connection remains open.");
        ProviderConnection.ClearAllPools();
#endif
    }
}

#if KORDA
[DbTable("business_result_orders", DescriptorName = "BusinessOrders")]
internal sealed record BusinessOrder([property: DbKey] long TenantId, [property: DbKey] long Id, string? Note);
[DbProjection(DescriptorName = "BusinessRows")]
#endif
public sealed record BusinessRow(long Id, string? Note);
public sealed record QueryArguments(long TenantId, int Limit);

internal readonly record struct Counts(long AllocatedBytes, long ThreadAllocatedBytes, int Gen0, int Gen1, int Gen2, int ThreadId, long Timestamp)
{
    internal static Counts Read() => new(GC.GetTotalAllocatedBytes(true), GC.GetAllocatedBytesForCurrentThread(),
        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Environment.CurrentManagedThreadId, Stopwatch.GetTimestamp());
}
internal sealed record AllocationWindow(string Name, Counts Start, Counts End, int Planned, int Issued, int Succeeded,
    int Failed, long Rows, ulong Digest, int ObservedIncompleteReturns, int CrossThreadAwaits, bool SynchronousControl, string? Error)
{
    public long RawAllocatedBytes => End.AllocatedBytes - Start.AllocatedBytes;
    public long? SameThreadAllocatedBytes => SynchronousControl && Start.ThreadId == End.ThreadId ? End.ThreadAllocatedBytes - Start.ThreadAllocatedBytes : null;
    public int Gen0Collections => End.Gen0 - Start.Gen0;
    public int Gen1Collections => End.Gen1 - Start.Gen1;
    public int Gen2Collections => End.Gen2 - Start.Gen2;
    public long? ExactFrameworkBytes => null;
}
internal sealed class MeasurementException(AllocationWindow window, Exception cause) : Exception("A buffered-query measurement failed.", cause)
{
    internal AllocationWindow Window { get; } = window;
}
internal sealed record GraphShape(int Dtos, int ListCapacity, int IndependentStrings, int NullStrings, int Utf16Characters, ulong Digest);
internal sealed class Report
{
    public string Schema { get; set; } = "DB-Perf-01.business-result-control.v1";
    public string Boundary { get; set; } = "Dynamic public buffered query construction, binding, complete 100-row materialization and validation/checksum, query/cursor disposal; session/source creation and return measured outside this interval. All managed threads are counted. Harness async wrappers and runtime activity remain in raw totals.";
    public string Attribution { get; set; } = "Business control recreates the actual returned List capacity, 100 DTOs and each independently allocated non-interned string after database drain. It is an isolated graph-construction control, not an exact allocation trace from the query. End-to-end minus this control is an unresolved framework/runtime/harness remainder, never an exact framework measurement. Discarded growth arrays are not in the retained graph and remain in that remainder.";
    public string GcScope { get; set; } = "Raw process Gen0/Gen1/Gen2 counts; collection events are not split into business/framework counts. No forced GC occurs in allocation intervals.";
    public string Scope { get; set; } = "Buffered Text DTO diagnostic only; no 35-case, throughput, native-allocation or long-segment credit. Numeric streaming is covered by the separately source-bound allocation probe.";
    public string Route { get; set; } = "";
    public string Provider { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string PrimaryPackagePath { get; set; } = "";
    public string PrimaryPackageSha256 { get; set; } = "";
    public string ExecutableSha256 { get; set; } = "";
    public string BuildReceiptPath { get; set; } = "";
    public string BuildReceiptSha256 { get; set; } = "";
    public Dictionary<string, string> ConsumerSources { get; set; } = [];
    public string Runtime { get; set; } = "";
    public int ProcessId { get; set; }
    public bool NativeAot { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public int WarmupOperations { get; set; }
    public int OperationsPerWindow { get; set; }
    public string Sql { get; set; } = "";
    public string Parameters { get; set; } = "";
    public Dictionary<string, string> NativeImages { get; set; } = [];
    public GraphShape? Graph { get; set; }
    public List<AllocationWindow> EndToEnd { get; set; } = [];
    public List<AllocationWindow> BusinessControls { get; set; } = [];
    public ulong[]? NativeOwnersAfterDrain { get; set; }
    public string NativeOwnerScope { get; set; } = "";
    public bool DatabaseDisposed { get; set; }
    public bool Passed { get; set; }
    public string? Error { get; set; }
    public string? CleanupError { get; set; }
    public bool FrameworkWideZeroBytes => false;
}
[JsonSerializable(typeof(Report))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ProbeJson : JsonSerializerContext;

internal sealed class NativeCounts : IDisposable
{
    private readonly nint _module, _counter;
    internal string Path { get; }
    internal static string[] Mapped(string name) => File.ReadLines("/proc/self/maps")
        .Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length == 6 && System.IO.Path.GetFileName(parts[5]) == name)
        .Select(parts => parts[5]).Distinct().ToArray();
#if KORDA
    internal NativeCounts(DatabaseProvider provider)
    {
        string[] paths = Mapped(provider == DatabaseProvider.PostgreSql ? "libnw_database_postgresql.so" : "libnw_database_sqlite.so");
        if (paths.Length != 1) throw new InvalidOperationException("Expected exactly one mapped Korda native provider.");
        Path = paths[0]; _module = NativeLibrary.Load(Path);
        try { _counter = NativeLibrary.GetExport(_module, "nw_db_diagnostic_count_v1"); }
        catch { NativeLibrary.Free(_module); throw; }
    }
#else
    private NativeCounts() { Path = ""; _module = 0; _counter = 0; }
#endif
    internal unsafe ulong[] Read()
    {
        var values = new ulong[8];
        var call = (delegate* unmanaged[Cdecl]<uint, ulong>)_counter;
        for (uint i = 0; i < 8; i++) values[i] = call(i);
        return values;
    }
    public void Dispose() => NativeLibrary.Free(_module);
}
