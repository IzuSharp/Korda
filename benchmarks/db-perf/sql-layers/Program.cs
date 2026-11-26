using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;

internal static class Program
{
    private const long Tenant = 1, Id = 1024, Limit = 2;
    private static readonly Order Expected = new(Tenant, Id, Dataset.CreatedAt(Id), Dataset.CustomerId(Id),
        Dataset.Cents(Tenant, Id) / 100m, Dataset.Note(Id), 1, new(Dataset.Channel(Id), Dataset.Gift(Id)),
        new string('m', Id % 17 == 0 ? 2048 : 32));

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 6) throw new ArgumentException("FindLayers <connection> <original|qualified|limited> <timing|plan> <new-result.json> <warmup> <operations>");
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Run the actual Linux x64 Native AOT ELF.");
        string lane = args[1], phase = args[2], output = Path.GetFullPath(args[3]);
        int warmup = int.Parse(args[4], CultureInfo.InvariantCulture), operations = int.Parse(args[5], CultureInfo.InvariantCulture);
        if (lane is not ("original" or "qualified" or "limited") || phase is not ("timing" or "plan")
            || (phase == "timing" && (warmup != 1024 || operations != 8192))
            || (phase == "plan" && (warmup != 8 || operations != 8)))
            throw new ArgumentException("Select the frozen bounded timing or separate instrumentation contract.");
        if (File.Exists(output)) throw new IOException("Never replace a previous attempt.");
        using var process = Process.GetCurrentProcess();
        var report = new FindReport
        {
            Lane = lane, Phase = phase, StartedUtc = DateTimeOffset.UtcNow, ProcessId = Environment.ProcessId,
            NativeAot = true, Runtime = RuntimeInformation.FrameworkDescription, ExecutableSha256 = Hash(Environment.ProcessPath!),
            WarmupPlanned = warmup, Planned = operations, WorkloadSha256 = SqlContracts.WorkloadSha256,
            Expected = Expected, ExpectedRowDigest = Consume(Expected),
            ClientSql = lane switch { "original" => SqlContracts.Original, "qualified" => SqlContracts.Qualified, _ => SqlContracts.Limited },
            ExpectedServerSql = lane switch { "original" => SqlContracts.OriginalServer, "qualified" => SqlContracts.QualifiedServer, _ => SqlContracts.LimitedServer },
            Parameters = lane switch
            {
                "original" => [new(1, "tenant", "System.Int64", Tenant), new(2, "id", "System.Int64", Id)],
                "qualified" => [new(1, "p1", "System.Int64", Tenant), new(2, "p2", "System.Int64", Id)],
                _ => [new(1, "p1", "System.Int64", Tenant), new(2, "p2", "System.Int64", Id), new(3, "p3", "System.Int64", Limit)]
            },
        };
        NpgsqlConnection? connection = null;
        try
        {
            string receiptPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "build-receipt.json"));
            report.BuildReceiptPath = receiptPath;
            report.BuildReceiptSha256 = Hash(receiptPath);
            using (var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath)))
            {
                var root = receipt.RootElement;
                if (root.GetProperty("exit_code").GetInt32() != 0 || root.GetProperty("executable_sha256").GetString() != report.ExecutableSha256
                    || root.GetProperty("contract").GetProperty("workload_sha256").GetString() != report.WorkloadSha256)
                    throw new InvalidOperationException("Actual executable or workload differs from the external package build.");
                report.ConsumerSources = root.GetProperty("sources").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            }
            report.ProcessBeforeDatabase = Counters.Read(process);
            var settings = Settings(args[0]);
            settings.Pooling = true; settings.MinPoolSize = 0; settings.MaxPoolSize = 1;
            settings.MaxAutoPrepare = 0; settings.ApplicationName = "DB-Perf-01-Find-" + lane + "-" + phase;
            connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            report.DatabaseVersion = connection.ServerVersion;
            report.ServerBackendPid = await connection.QuerySingleAsync<int>("SELECT pg_backend_pid()");
            if (await connection.QuerySingleAsync<long>("SELECT tenants FROM db_perf_metadata WHERE schema_version=1") != 8
                || await connection.QuerySingleAsync<long>("SELECT orders_per_tenant FROM db_perf_metadata WHERE schema_version=1") != 1024
                || await connection.QuerySingleAsync<long>("SELECT count(*) FROM orders") != 8192)
                throw new InvalidOperationException("The unchanged full queries-v3 fixture is required.");
            Func<DbConnection, Task<Order>> query = lane switch
            {
                "original" => QueryOriginal,
                "qualified" => QueryQualified,
                _ => QueryLimited
            };
            for (int i = 0; i < warmup; i++)
            {
                report.WarmupIssued++;
                _ = Consume(await query(connection).ConfigureAwait(false));
                report.WarmupSucceeded++;
            }
            var latencies = new long[operations];
            report.Before = Counters.Read(process);
            ulong digest = 14695981039346656037UL;
            try
            {
                for (int i = 0; i < operations; i++)
                {
                    report.Issued++;
                    long start = Stopwatch.GetTimestamp();
                    int thread = Environment.CurrentManagedThreadId;
                    var pending = query(connection);
                    if (!pending.IsCompleted) report.ObservedIncompleteReturns++;
                    var row = await pending.ConfigureAwait(false);
                    if (thread != Environment.CurrentManagedThreadId) report.CrossThreadAwaits++;
                    ulong one = Consume(row);
                    digest = unchecked((digest ^ one) * 1099511628211UL);
                    latencies[report.Succeeded] = Stopwatch.GetTimestamp() - start;
                    report.Succeeded++; report.Rows++;
                    GC.KeepAlive(row);
                }
            }
            finally
            {
                report.After = Counters.Read(process);
                report.Digest = digest;
                report.Failed = report.Issued - report.Succeeded;
                report.LatencyTicks = latencies;
            }
            report.PostMeasurementQueriesIssued++;
            _ = Consume(await query(connection).ConfigureAwait(false));
            report.PersistentTargetVerified = true;
            report.Passed = report.WarmupSucceeded == warmup && report.Succeeded == operations;
        }
        catch (Exception error)
        {
            report.Error = error.GetType().FullName + ": " + error.Message;
            report.Passed = false;
        }
        finally
        {
            if (connection is not null)
            {
                try
                {
                    await connection.DisposeAsync();
                    report.ConnectionDisposed = connection.State == System.Data.ConnectionState.Closed;
                    NpgsqlConnection.ClearAllPools();
                    report.PoolsCleared = true;
                }
                catch (Exception error) { report.CleanupError = error.GetType().FullName + ": " + error.Message; report.Passed = false; }
            }
            report.ProcessAfterDisposalBeforeSerialization = Counters.Read(process);
            report.FinishedUtc = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, report, PerfJson.Default.FindReport);
        }
        return report.Passed && report.Error is null && report.CleanupError is null && report.ConnectionDisposed && report.PoolsCleared ? 0 : 1;
    }

    [CacheCommand(false)]
    private static Task<Order> QueryOriginal(DbConnection connection)
        => connection.QuerySingleAsync<Order>(SqlContracts.Original, new OriginalKey(Tenant, Id));
    [CacheCommand(false)]
    private static Task<Order> QueryQualified(DbConnection connection)
        => connection.QuerySingleAsync<Order>(SqlContracts.Qualified, new QualifiedKey(Tenant, Id));
    [CacheCommand(false)]
    private static Task<Order> QueryLimited(DbConnection connection)
        => connection.QuerySingleAsync<Order>(SqlContracts.Limited, new LimitedKey(Tenant, Id, Limit));

    private static ulong Consume(Order value)
    {
        if (value != Expected) throw new InvalidOperationException("The complete Order/decimal/typed JSON result changed.");
        var digest = Digest.Create();
        digest.Integer(value.TenantId); digest.Integer(value.Id); digest.Integer(value.CreatedAt);
        digest.Boolean(value.CustomerId.HasValue); if (value.CustomerId.HasValue) digest.Integer(value.CustomerId.Value);
        digest.Money(value.Total); digest.Text(value.Note); digest.Integer(value.Version);
        digest.Text(value.Settings.Channel); digest.Boolean(value.Settings.Gift); digest.Text(value.InternalMemo);
        return digest.Value;
    }

    private static NpgsqlConnectionStringBuilder Settings(string connection)
    {
        if (!connection.StartsWith("postgresql://", StringComparison.Ordinal)) return new(connection);
        var uri = new Uri(connection);
        if (uri.UserInfo.Contains(':')) throw new ArgumentException("Use an explicit connection string when credentials require escaping.");
        return new() { Host = uri.Host, Port = uri.Port < 0 ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(uri.UserInfo), Database = uri.AbsolutePath.Trim('/'), SslMode = SslMode.Disable };
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}

public sealed record OriginalKey(long tenant, long id);
public sealed record QualifiedKey(long p1, long p2);
public sealed record LimitedKey(long p1, long p2, long p3);
internal sealed record ParameterContract(int Ordinal, string Name, string ClrType, long Value);
internal readonly record struct Counters(long AllocatedBytes, int Gen0, int Gen1, int Gen2, long Timestamp,
    double UserCpuSeconds, double SystemCpuSeconds)
{
    internal static Counters Read(Process process)
    {
        process.Refresh();
        double user = process.UserProcessorTime.TotalSeconds, system = process.PrivilegedProcessorTime.TotalSeconds;
        return new(GC.GetTotalAllocatedBytes(true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            Stopwatch.GetTimestamp(), user, system);
    }
}
internal sealed class FindReport
{
    public string Schema => "DB-Perf-01.find-sql-layer.v1";
    public string Scope => "Same Dapper.AOT QuerySingleAsync full Order in every lane. New diagnostic only; not formal workload or performance acceptance. Plan logging uses separate processes.";
    public string CounterScope => "Precise cumulative process allocation and GC snapshots cover all managed threads. Query loop includes new parameter objects, complete Order/decimal/JSON materialization and full digest consumption. BCL CPU endpoint observation may contribute small common harness allocations. Setup/disposal and report serialization remain outside the query window; no exact framework allocation claim.";
    public string Lane { get; set; } = "";
    public string Phase { get; set; } = "";
    public bool NativeAot { get; set; }
    public int ProcessId { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public string Runtime { get; set; } = "";
    public string DatabaseVersion { get; set; } = "";
    public int ServerBackendPid { get; set; }
    public int MaxAutoPrepare => 0;
    public int LogicalConnections => 1;
    public bool CommandCaching => false;
    public string ExecutableSha256 { get; set; } = "";
    public string BuildReceiptPath { get; set; } = "";
    public string BuildReceiptSha256 { get; set; } = "";
    public Dictionary<string, string> ConsumerSources { get; set; } = [];
    public string WorkloadSha256 { get; set; } = "";
    public string ClientSql { get; set; } = "";
    public string ExpectedServerSql { get; set; } = "";
    public ParameterContract[] Parameters { get; set; } = [];
    public string ParameterTypeScope => "Actual call arguments are System.Int64 and original Dapper generated binding is retained; server value logs are captured separately. Raw server parameter OIDs are not instrumented.";
    public Order? Expected { get; set; }
    public ulong ExpectedRowDigest { get; set; }
    public int WarmupPlanned { get; set; }
    public int WarmupIssued { get; set; }
    public int WarmupSucceeded { get; set; }
    public int WarmupFailed => WarmupIssued - WarmupSucceeded;
    public int Planned { get; set; }
    public int Issued { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Retries => 0;
    public long Rows { get; set; }
    public ulong Digest { get; set; }
    public int ObservedIncompleteReturns { get; set; }
    public int CrossThreadAwaits { get; set; }
    public Counters? Before { get; set; }
    public Counters? After { get; set; }
    public Counters? ProcessBeforeDatabase { get; set; }
    public Counters? ProcessAfterDisposalBeforeSerialization { get; set; }
    public long StopwatchFrequency => Stopwatch.Frequency;
    public long[] LatencyTicks { get; set; } = [];
    public bool PersistentTargetVerified { get; set; }
    public int PostMeasurementQueriesIssued { get; set; }
    public bool ConnectionDisposed { get; set; }
    public bool PoolsCleared { get; set; }
    public string OwnerScope => "Application connection disposed and Npgsql pools cleared; internal provider owners are not instrumented.";
    public bool Passed { get; set; }
    public string? Error { get; set; }
    public string? CleanupError { get; set; }
    public long? ExactFrameworkBytes => null;
}
[JsonSerializable(typeof(OrderSettings))]
[JsonSerializable(typeof(FindReport))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class PerfJson : JsonSerializerContext;
