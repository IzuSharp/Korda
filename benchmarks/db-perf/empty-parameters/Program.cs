using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Query;

if (args.Length != 5 || RuntimeFeature.IsDynamicCodeSupported)
    throw new ArgumentException("Native AOT only: <result.json> <query-context-source> <source-base> <postgres-uri> <sqlite-file>");
const int Warmup = 1024, Operations = 16384;
var observations = new List<Observation>();
var errors = new List<ErrorObservation>();
var checks = new List<string>();
var nativeHashes = new Dictionary<string, string>();
var nativeMappings = new Dictionary<string, string[]>();
var nativeLiveCounts = new Dictionary<string, ulong[]>();
using var process = Process.GetCurrentProcess();
var startedUtc = DateTimeOffset.UtcNow;
var builder = new TableBuilder<Item>("EmptyParameterProbe");
var idColumn = builder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
var table = builder.Build(row => new(row.Get(idColumn)), (row, value) => row.Set(idColumn, value.Id));
foreach (var provider in new[] { "sqlite", "postgresql" })
{
    var native = Path.Combine(AppContext.BaseDirectory, "libnw_database_" + provider + ".so");
    nativeHashes.Add(provider, Hash(native));
    using var diagnostic = new OwnerDiagnostic(native);
    var source = provider == "sqlite" ? SqliteDataSource.Create(args[4]) : PostgreSqlDataSource.Create(args[3], options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
    var profile = source.Binding.SemanticProfile;
    var session = source.OpenSessionAsync().GetAwaiter().GetResult();
    var liveCounts = diagnostic.Counts();
    Require(liveCounts[0] == 1 && liveCounts[1] == 1, "Diagnostic observes the actual live source and session");
    nativeLiveCounts.Add(provider, liveCounts);
    var mappings = File.ReadAllLines("/proc/self/maps").Where(line => line.Contains("libnw_database_" + provider + ".so", StringComparison.Ordinal)).ToArray();
    Require(mappings.Length > 0 && mappings.All(line => line.EndsWith(" " + native, StringComparison.Ordinal)), "Only the SDK-selected native path is loaded");
    nativeMappings.Add(provider, mappings);
    try
    {
        using var emptyQuery = session.From(table);
        var context = emptyQuery.Context;
        var expected = Golden(profile);
        Func<byte[]> encodeEmpty = () => context.Values(null);
        _ = GC.GetTotalAllocatedBytes(true); _ = Stopwatch.GetTimestamp();
        // This is the first dynamic empty-frame access after real source/session/model setup,
        // not process cold start. Do not call the encoder to prepare a golden first.
        observations.Add(Measure(provider, profile, "dynamic-null-cold-first-access", encodeEmpty, expected, 1, 0));
        observations.Add(Measure(provider, profile, "dynamic-null-warm", encodeEmpty, expected, Operations, Warmup));

        var suppliedEmpty = new QueryParameters();
        observations.Add(Measure(provider, profile, "dynamic-explicit-empty-fallback", () => context.Values(suppliedEmpty), expected, Operations, Warmup));
        var prepared = new PreparedParameterLayout(source, context.Parameters.Values);
        observations.Add(Measure(provider, profile, "prepared-empty-unchanged", () => prepared.Encode(source, null), expected, Operations, Warmup));
        var legacy = ParameterDefinition.Encode(source, context.Parameters.Values, null);
        Require(legacy.AsSpan().SequenceEqual(expected), "independent golden equals original encoder for " + provider);

        using var parameterQuery = session.From(table);
        var parameter = parameterQuery.Parameter("id", Codecs.Int64);
        var supplied = new QueryParameters().Set(parameter, 17L);
        var nonemptyGolden = ParameterDefinition.Encode(source, parameterQuery.Context.Parameters.Values, supplied);
        observations.Add(Measure(provider, profile, "dynamic-one-parameter-unchanged", () => parameterQuery.Context.Values(supplied), nonemptyGolden, Operations, Warmup));
        ObserveError(provider, "declared-missing-null", () => parameterQuery.Context.Values(null));
        ObserveError(provider, "declared-missing-explicit-empty", () => parameterQuery.Context.Values(suppliedEmpty));
        ObserveError(provider, "empty-shape-extra-supplied", () => context.Values(supplied));
        using var anotherQuery = session.From(table);
        var anotherParameter = anotherQuery.Parameter("other", Codecs.Int64);
        var wrongIdentity = new QueryParameters().Set(anotherParameter, 17L);
        ObserveError(provider, "declared-wrong-identity", () => parameterQuery.Context.Values(wrongIdentity));
        ObserveError(provider, "prepared-empty-extra-supplied", () => prepared.Encode(source, supplied));

        // Distinct contexts obey the public single-writer query rule. Correctness only:
        // thread creation, synchronization, and result storage are outside measured windows.
        var contexts = new Query<Item>[4];
        var threads = new Thread[4];
        var results = new byte[4][];
        var workerErrors = new Exception?[4];
        using var barrier = new Barrier(4);
        for (var index = 0; index < contexts.Length; index++) contexts[index] = session.From(table);
        for (var index = 0; index < threads.Length; index++)
        {
            var captured = index;
            threads[index] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    for (var iteration = 0; iteration < 256; iteration++)
                    {
                        var bytes = contexts[captured].Context.Run(() => contexts[captured].Context.Values(null), mutable: false);
                        Require(bytes.AsSpan().SequenceEqual(expected), "parallel independent-context frame changed");
                        results[captured] = bytes;
                    }
                }
                catch (Exception error) { workerErrors[captured] = error; }
            });
            threads[index].Start();
        }
        for (var index = 0; index < threads.Length; index++) threads[index].Join();
        for (var index = 0; index < contexts.Length; index++) contexts[index].Dispose();
        Require(workerErrors.All(error => error is null), "parallel independent-context correctness");
        checks.Add(provider + "/distinct-context-concurrent-full-byte-validation");
        checks.Add(provider + "/same-frame-reference-across-contexts=" + results.All(bytes => ReferenceEquals(results[0], bytes)));
        Require(context.Values(null).AsSpan().SequenceEqual(expected), "frame unchanged after validation and concurrency");

        // Actual driver acceptance and lazy captures are checked after allocation windows.
        session.ExecuteAsync(SqlCommand.Create("CREATE TABLE IF NOT EXISTS \"EmptyParameterProbe\" (\"Id\" BIGINT PRIMARY KEY)", kind: SqlCommandKind.Mutation)).GetAwaiter().GetResult();
        session.ExecuteAsync(SqlCommand.Create("DELETE FROM \"EmptyParameterProbe\"", kind: SqlCommandKind.Mutation)).GetAwaiter().GetResult();
        session.ExecuteAsync(SqlCommand.Create("INSERT INTO \"EmptyParameterProbe\" VALUES (1),(2)", kind: SqlCommandKind.Mutation)).GetAwaiter().GetResult();
        using (var query = session.From(table)) Require(query.OrderBy(idColumn).SelectAll().ToListAsync().GetAwaiter().GetResult().Select(row => row.Id).SequenceEqual(new long[] { 1, 2 }), "public dynamic list result");
        using (var left = session.From(table))
        using (var right = session.From(table))
        {
            var first = left.OrderBy(idColumn).Select(left.Column(idColumn)).AsAsyncEnumerable();
            var second = right.OrderBy(idColumn).Select(right.Column(idColumn)).AsAsyncEnumerable();
            Require(Drain(first) == 3 && Drain(second) == 3, "independent lazy streams share only immutable bytes");
        }
        Require(context.Values(null).AsSpan().SequenceEqual(expected), "native execute did not mutate frame");
        checks.Add(provider + "/real-dynamic-driver-result-and-delayed-stream-captures");
    }
    finally
    {
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        source.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    var counts = diagnostic.WaitZero();
    checks.Add(provider + "/all-six-native-owner-counters-zero=" + string.Join(',', counts));
}
var finishedUtc = DateTimeOffset.UtcNow;
using var output = File.Create(args[0]);
using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
writer.WriteStartObject();
writer.WriteString("schema", "db-perf-empty-parameters-v1");
writer.WriteString("scope", "Native AOT internal QueryContext.Values seam with real opened sources/sessions and immutable prebuilt query fixtures. Whole-process allocation includes runtime/background noise. Public database correctness runs after allocation windows. This is not PackageReference, E2E allocation, or qualified throughput/latency acceptance.");
writer.WriteString("sourceBaseCommit", args[2]); writer.WriteString("queryContextSource", Path.GetFullPath(args[1]));
writer.WriteString("queryContextSha256", Hash(args[1])); writer.WriteString("executableSha256", Hash(Environment.ProcessPath!));
writer.WriteString("runtime", RuntimeInformation.FrameworkDescription); writer.WriteBoolean("nativeAot", true);
writer.WriteNumber("processId", Environment.ProcessId); writer.WriteString("startedUtc", startedUtc); writer.WriteString("finishedUtc", finishedUtc);
writer.WriteNumber("stopwatchFrequency", Stopwatch.Frequency); writer.WriteNumber("businessResultAllocatedBytesWithinSeam", 0);
writer.WriteString("gcScope", "Each collection generation belongs to the whole shared process heap; no business/framework split is assigned to GC counts.");
writer.WriteString("coldScope", "First QueryContext.Values(null) access after actual source, session and model creation; not first process initialization, not error/cancellation allocation.");
writer.WriteString("cpuScope", "Raw process user/system endpoints are recorded, but short timings on this shared Debian diagnostic host are not formal CPU/latency evidence.");
writer.WriteStartObject("nativeImages"); foreach (var pair in nativeHashes) writer.WriteString(pair.Key, pair.Value); writer.WriteEndObject();
writer.WriteStartObject("nativeLoadedMappings"); foreach (var pair in nativeMappings) { writer.WriteStartArray(pair.Key); foreach (var line in pair.Value) writer.WriteStringValue(line); writer.WriteEndArray(); } writer.WriteEndObject();
writer.WriteStartObject("nativeLiveCounts"); foreach (var pair in nativeLiveCounts) { writer.WriteStartArray(pair.Key); foreach (var value in pair.Value) writer.WriteNumberValue(value); writer.WriteEndArray(); } writer.WriteEndObject();
writer.WriteStartArray("observations");
foreach (var item in observations)
{
    writer.WriteStartObject(); writer.WriteString("provider", item.Provider); writer.WriteNumber("semanticProfile", item.Profile); writer.WriteString("name", item.Name);
    writer.WriteNumber("warmup", item.Warmup); writer.WriteNumber("operations", item.Operations); writer.WriteNumber("rows", 0);
    writer.WriteNumber("frameBytes", item.Frame.Length); writer.WriteString("fullFrameHex", Convert.ToHexStringLower(item.Frame));
    writer.WriteNumber("allocatedBefore", item.Before); writer.WriteNumber("allocatedAfter", item.After); writer.WriteNumber("allocatedDelta", item.After - item.Before);
    writer.WriteNumber("bytesPerOperation", (double)(item.After - item.Before) / item.Operations);
    writer.WriteNumber("gc0Before", item.Gc0Before); writer.WriteNumber("gc1Before", item.Gc1Before); writer.WriteNumber("gc2Before", item.Gc2Before);
    writer.WriteNumber("gc0After", item.Gc0After); writer.WriteNumber("gc1After", item.Gc1After); writer.WriteNumber("gc2After", item.Gc2After);
    writer.WriteNumber("startTicks", item.StartTicks); writer.WriteNumber("endTicks", item.EndTicks); writer.WriteNumber("checksum", item.Checksum);
    writer.WriteNumber("userCpuSecondsBefore", item.UserBefore); writer.WriteNumber("systemCpuSecondsBefore", item.SystemBefore);
    writer.WriteNumber("userCpuSecondsAfter", item.UserAfter); writer.WriteNumber("systemCpuSecondsAfter", item.SystemAfter); writer.WriteEndObject();
}
writer.WriteEndArray(); writer.WriteStartArray("exceptions");
foreach (var item in errors) { writer.WriteStartObject(); writer.WriteString("provider", item.Provider); writer.WriteString("name", item.Name); writer.WriteString("type", item.Type); writer.WriteString("message", item.Message); writer.WriteEndObject(); }
writer.WriteEndArray(); writer.WriteStartArray("checks"); foreach (var check in checks) writer.WriteStringValue(check); writer.WriteEndArray(); writer.WriteBoolean("passed", true); writer.WriteEndObject();

Observation Measure(string provider, uint profile, string name, Func<byte[]> action, byte[] expected, int operations, int warmup)
{
    for (var index = 0; index < warmup; index++) _ = Consume(action(), expected);
    process.Refresh(); var userBefore = process.UserProcessorTime.TotalSeconds; var systemBefore = process.PrivilegedProcessorTime.TotalSeconds;
    var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
    var before = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp(); long checksum = 0;
    for (var index = 0; index < operations; index++) checksum += Consume(action(), expected);
    var end = Stopwatch.GetTimestamp(); var after = GC.GetTotalAllocatedBytes(true);
    var after0 = GC.CollectionCount(0); var after1 = GC.CollectionCount(1); var after2 = GC.CollectionCount(2);
    process.Refresh(); var userAfter = process.UserProcessorTime.TotalSeconds; var systemAfter = process.PrivilegedProcessorTime.TotalSeconds;
    Require(checksum == (long)operations * Consume(expected, expected), "complete frame checksum changed");
    return new(provider, profile, name, operations, warmup, expected, before, after, gc0, gc1, gc2, after0, after1, after2, start, end, checksum, userBefore, systemBefore, userAfter, systemAfter);
}
void ObserveError(string provider, string name, Action action)
{
    try { action(); } catch (ArgumentException error) { errors.Add(new(provider, name, error.GetType().FullName!, error.Message)); return; }
    throw new InvalidOperationException("Expected ArgumentException: " + provider + "/" + name);
}
static byte[] Golden(uint profile)
{
    // Independent fixed-layout construction, not ParameterDefinition or DbWire.Encode.
    var result = new byte[96]; Encoding.ASCII.GetBytes("NWDB").CopyTo(result, 0);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), 96); BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), 96);
    SHA256.HashData("{\"version\":1,\"parameters\":[]}"u8).CopyTo(result, 24);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56), 1); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(76), profile);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(80), 96); BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(88), 96);
    return result;
}
[MethodImpl(MethodImplOptions.NoInlining)]
static long Consume(byte[] bytes, byte[] expected)
{
    if (!bytes.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Complete frame differs from golden.");
    long sum = 0; foreach (var value in bytes) sum += value; return sum;
}
static long Drain(IAsyncEnumerable<long> stream)
{
    var enumerator = stream.GetAsyncEnumerator(); long sum = 0;
    try { while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) sum += enumerator.Current; }
    finally { enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    return sum;
}
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
internal sealed record Item(long Id);
internal sealed record ErrorObservation(string Provider, string Name, string Type, string Message);
internal sealed record Observation(string Provider, uint Profile, string Name, int Operations, int Warmup, byte[] Frame,
    long Before, long After, int Gc0Before, int Gc1Before, int Gc2Before, int Gc0After, int Gc1After, int Gc2After,
    long StartTicks, long EndTicks, long Checksum, double UserBefore, double SystemBefore, double UserAfter, double SystemAfter);
internal sealed unsafe class OwnerDiagnostic : IDisposable
{
    private readonly nint _library, _count;
    internal OwnerDiagnostic(string path) { _library = NativeLibrary.Load(path); _count = NativeLibrary.GetExport(_library, "nw_db_diagnostic_count_v1"); }
    internal ulong[] Counts()
    {
        var result = new ulong[6];
        for (uint index = 0; index < 6; index++) result[index] = ((delegate* unmanaged[Cdecl]<uint, ulong>)_count)(index);
        return result;
    }
    internal ulong[] WaitZero()
    {
        var result = new ulong[6];
        for (var attempt = 0; attempt < 100; attempt++)
        {
            for (uint index = 0; index < 6; index++) result[index] = ((delegate* unmanaged[Cdecl]<uint, ulong>)_count)(index);
            if (result.All(value => value == 0)) return result;
            Thread.Sleep(10);
        }
        throw new InvalidOperationException("Native owners did not drain: " + string.Join(',', result));
    }
    public void Dispose() => NativeLibrary.Free(_library);
}
